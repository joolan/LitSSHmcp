using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace LitSSHmcp.Agent;

/// <summary>一台 MCP 服务器的连接状态快照（供连接器列表展示：工具数 / 连接错误）。</summary>
public sealed record McpHostStatus(string ServerId, string Name, bool Builtin, bool Connected, int ToolCount, string? Error);

/// <summary>
/// 一次运行中的 AI 运维助手：持有 MCP 工具宿主 + 对话会话 + 可选的长期记忆。调用方负责在切换配置/关闭时 Dispose。
/// **MCP 宿主与工具装配只做一次**；切换会话/切换模型用 <see cref="ResetSession"/> 仅重建对话会话，不重连 MCP。
/// </summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    /// <summary>本次运行持有的全部 MCP 宿主（内置 + 启用的外部，按连接顺序）。</summary>
    public IReadOnlyList<McpToolHost> Hosts { get; }
    public AgentSession Session { get; private set; }
    public AgentProviderConfig Provider { get; private set; }
    public AgentMemoryService? Memory { get; }
    public int ToolCount => Session.ToolCount;

    /// <summary>
    /// 本次运行装配的工作区主根（会话目录或共享工作区），由 <see cref="StartAsync"/> 赋值。
    /// 调用方（App）据此判断切换会话后是否需要重建运行时——工作区根在装配期烧进工具与提示词，运行中不可变。
    /// </summary>
    public string WorkspaceRoot { get; private set; } = string.Empty;

    /// <summary>启用但未能连接的外部 MCP（名称 + 原因）；为空=全部正常。</summary>
    public IReadOnlyList<string> McpWarnings { get; }

    /// <summary>长期记忆被禁用的原因（已启用但配置不全/初始化失败时非空；未启用时为 null）。</summary>
    public string? MemoryDisabledReason { get; }

    /// <summary>各 MCP 服务器的连接状态（内置 + 启用的外部，按配置顺序；停用的不列出）。</summary>
    public IReadOnlyList<McpHostStatus> McpHostStatuses { get; }

    /// <summary>本次会话实际暴露的 MCP 工具能力域（供 UI 技能选择器判定可用性）。</summary>
    public IReadOnlySet<string> AvailableGroups =>
        _baseTools.Select(t => t.ToolGroup).Where(g => g is not null).Select(g => g!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly AgentConfig _config;
    private string _prompt;
    private readonly Func<IEnumerable<string>?, AgentPromptContext?, string> _promptBuilder;
    private readonly string? _readOnlyNote;
    private readonly List<IAgentTool> _baseTools;
    private readonly List<IAgentTool> _subTools;
    private readonly IAgentTool? _roundNotifier;
    private readonly Action<IReadOnlyList<PlanItem>>? _onPlan;
    private readonly Func<string, IReadOnlyCollection<string>?, CancellationToken, Task<string?>>? _recall;
    private readonly Func<string, CancellationToken, Task<AssetGraphContextResult?>>? _graphContext;
    private readonly SpillStore? _spill;
    private List<string> _activeSkillIds = new();
    private AgentPromptContext? _activeAgent;
    private bool _planMode;
    private IReadOnlyDictionary<string, AgentToolPolicy>? _toolPolicy;

    private const string ReadOnlyNote =
        "\n\n【只读模式（全局配置）】仅可调用只读工具；禁止任何写操作（命令/SQL/文件写入/重启等）。"
        + "如用户要求写操作，说明当前处于只读模式并给出替代建议。";

    private AgentRuntime(
        IReadOnlyList<McpToolHost> hosts,
        IReadOnlyList<string> mcpWarnings,
        IReadOnlyList<McpHostStatus> mcpHostStatuses,
        AgentProviderConfig provider,
        AgentMemoryService? memory,
        string? memoryDisabledReason,
        AgentConfig config,
        Func<IEnumerable<string>?, AgentPromptContext?, string> promptBuilder,
        AgentPromptContext? initialAgent,
        List<IAgentTool> baseTools,
        List<IAgentTool> subTools,
        IAgentTool? roundNotifier,
        Action<IReadOnlyList<PlanItem>>? onPlan,
        Func<string, IReadOnlyCollection<string>?, CancellationToken, Task<string?>>? recall,
        Func<string, CancellationToken, Task<AssetGraphContextResult?>>? graphContext,
        SpillStore? spill,
        IEnumerable<ChatMessage>? history)
    {
        Hosts = hosts;
        McpWarnings = mcpWarnings;
        McpHostStatuses = mcpHostStatuses;
        Provider = provider;
        Memory = memory;
        MemoryDisabledReason = memoryDisabledReason;
        _config = config;
        _readOnlyNote = config.ReadOnly ? ReadOnlyNote : null;
        _promptBuilder = promptBuilder;
        _activeAgent = initialAgent;
        _prompt = ApplyReadOnlyNote(promptBuilder(null, initialAgent));
        _baseTools = baseTools;
        _subTools = subTools;
        _roundNotifier = roundNotifier;
        _onPlan = onPlan;
        _recall = recall;
        _graphContext = graphContext;
        _spill = spill;
        Session = BuildSession(provider, history);
    }

    /// <summary>当前是否处于计划模式（计划模式下只暴露只读工具）。</summary>
    public bool PlanMode => _planMode;

    /// <summary>可配置审批策略的 MCP 工具目录（供「工具审批策略」窗口）。</summary>
    public IReadOnlyList<ToolPolicyItem> ToolCatalog =>
        _baseTools.Where(t => t.ToolGroup is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new ToolPolicyItem(t.Name, t.ToolGroup!, t.ReadOnly, t.Destructive))
            .ToList();

    /// <summary>设置工具级审批策略（会话级，Agent 层）并应用到当前会话。</summary>
    public void SetToolPolicy(IReadOnlyDictionary<string, AgentToolPolicy>? policy)
    {
        _toolPolicy = policy;
        Session.SetToolPolicy(policy);
    }

    private string ApplyReadOnlyNote(string prompt) => _readOnlyNote is null ? prompt : prompt + _readOnlyNote;

    private string BuildPrompt() =>
        ApplyReadOnlyNote(_promptBuilder(_activeSkillIds.Count == 0 ? null : _activeSkillIds, _activeAgent));

    /// <summary>
    /// 切换激活技能：重算系统提示并就地替换当前会话的第一条 System 消息（不重连 MCP、不丢历史）。
    /// 应在会话空闲时调用。
    /// </summary>
    public void SetActiveSkills(IEnumerable<string>? activeSkillIds)
    {
        _activeSkillIds = activeSkillIds?.ToList() ?? new();
        _prompt = BuildPrompt();
        Session.SetSystemPrompt(_prompt);
    }

    /// <summary>
    /// 切换当前智能体（缓存友好）：智能体段位于系统提示词**最末尾**，切换只使尾部前缀缓存失效，
    /// 其前的稳定前缀（基础约定/技能/MCP 说明）保持命中。不改工具集、不重连 MCP、不丢历史。
    /// </summary>
    public void SetActiveAgent(AgentPromptContext? agent)
    {
        _activeAgent = agent;
        _prompt = BuildPrompt();
        Session.SetSystemPrompt(_prompt);
    }

    /// <summary>
    /// 切换计划模式（缓存友好）：**不改系统提示、不改工具集**，仅在会话上设一个运行期标志——
    /// 每轮把"计划模式"提示附在用户消息尾部，并在调用前拦截写工具。因此切换不会使 prompt 前缀缓存失效。
    /// </summary>
    public void SetPlanMode(bool planMode)
    {
        if (_planMode == planMode)
            return;
        _planMode = planMode;
        Session.SetPlanMode(planMode);
    }

    /// <summary>用当前配置与工具重新构建对话会话（仅换历史/模型，不重连 MCP）。</summary>
    public void ResetSession(AgentProviderConfig provider, IEnumerable<ChatMessage>? history)
    {
        Provider = provider;
        Session = BuildSession(provider, history);
    }

    /// <summary>构建对话会话。计划模式不改变工具集（保持前缀稳定），只设置运行期标志。</summary>
    private AgentSession BuildSession(AgentProviderConfig provider, IEnumerable<ChatMessage>? history)
    {
        var client = ChatClientFactory.Create(provider);
        var tools = new List<IAgentTool>(_baseTools);
        AgentSession? session = null;

        Func<long, long, bool>? budgetCheck = null;
        SessionCostBudget? budget = null;
        var limit = _config.Budget?.MaxSessionCost ?? 0;
        var inputPrice = provider.InputPricePerMTok;
        var outputPrice = provider.OutputPricePerMTok;
        if (limit > 0 && (inputPrice > 0 || outputPrice > 0))
        {
            budget = new SessionCostBudget(limit, inputPrice, outputPrice);
            budgetCheck = (_, _) => budget.Check();
        }

        if (_config.EnableSubAgent)
        {
            tools.Add(SubAgents.Create(client, provider, _config, _subTools, _readOnlyNote,
                onUsage: (input, output) => session?.AddChildUsage(input, output),
                budgetCheck: budgetCheck,
                onUsageDelta: budget is null ? null : budget.Add,
                toolPolicyProvider: () => _toolPolicy,
                planModeProvider: () => _planMode));
        }
        if (_onPlan is not null)
            tools.Add(PlanTools.Create(_onPlan));
        session = new AgentSession(client, provider, tools, _prompt, _config, history, _recall, _spill,
            roundNotifier: _roundNotifier, graphContext: _graphContext, budgetCheck: budgetCheck,
            onUsageDelta: budget is null ? null : budget.Add);
        session.SetToolPolicy(_toolPolicy);
        session.SetPlanMode(_planMode);
        return session;
    }

    /// <summary>
    /// 连接 MCP 服务器、装配工具与系统提示、构建对话会话（含可选长期记忆）。
    /// <paramref name="sessionWorkspaceDir"/>：本会话工作目录（需求 D；空=共享工作区，现状行为）；
    /// <paramref name="ownerUserId"/>：会话归属用户，随宿主 spawn 注入 MCP 子进程做审计归因（D1，空=local）。
    /// <paramref name="ownerRole"/>：会话用户角色（Web 注入 admin/user），供子进程判断能否查看全局历史/会话。
    /// <paramref name="scopeSpillByOwner"/>：大结果落盘按 owner 子目录隔离（Web 多用户必开；桌面默认关闭）。
    /// <paramref name="scopeMemoryByOwner"/>：长期记忆按 owner/scope 隔离（Web 多用户必开；桌面默认关闭）。
    /// </summary>
    public static async Task<AgentRuntime> StartAsync(
        AgentConfig config,
        AgentProviderConfig provider,
        string? bundledSkillsDir,
        IEnumerable<ChatMessage>? history = null,
        Action<IReadOnlyList<PlanItem>>? onPlan = null,
        IReadOnlyCollection<string>? activeSkillIds = null,
        bool planMode = false,
        AgentPromptContext? activeAgent = null,
        CancellationToken ct = default,
        IEnumerable<IAgentTool>? extraTools = null,
        string? assetGraph = null,
        string? sessionWorkspaceDir = null,
        string? ownerUserId = null,
        IReadOnlyCollection<string>? enabledMcpIds = null,
        string? ownerUsername = null,
        string? ownerRole = null,
        bool scopeSpillByOwner = false,
        bool scopeMemoryByOwner = false)
    {
        // D8 启用层：会话级连接器子集只在全局启用集合内收窄（null=继承全局默认集合；全局停用恒出局）
        var servers = McpSubsetPolicy.EffectiveServers(AgentMcpServerConfig.Normalize(config.McpServers), enabledMcpIds);
        var builtin = servers.FirstOrDefault(s => s.Builtin);
        var hosts = new List<McpToolHost>();
        var externalHosts = new List<(AgentMcpServerConfig Server, McpToolHost Host)>();
        var mcpWarnings = new List<string>();
        var infoById = new Dictionary<string, ExternalMcpServerInfo>(StringComparer.Ordinal);
        var serverInstructions = new List<string>();
        McpToolHost? builtinHost = null;

        try
        {
            // 1) 内置 LitSSH MCP：默认启用；停用则**跳过 exe 解析**（缺 exe 不再导致整个助手不可用）
            if (builtin is { Enabled: true })
            {
                var launch = AgentPaths.ResolveLaunch(config.McpServerPath)
                    ?? throw new InvalidOperationException(
                        "未找到 MCP 服务器(LitSSHmcp.McpServer.exe): 请将服务器放到 App 的 mcp/ 目录, 或在「AI 助手设置」中指定路径。");
                // 归因身份与会话工作目录都由宿主 spawn 时注入（评估报告 D1 / §10.A.5、需求 D / §5.3.1），子进程不自行决定；
                // ownerUsername/ownerRole 供子进程匹配授权矩阵与数据可见范围（M3-4 / P0-2，仅 Web 宿主注入）
                var host = McpToolHost.ForBuiltin(launch, ownerUserId, sessionWorkspaceDir, ownerUsername, ownerRole);
                try
                {
                    await host.ConnectAsync(ct);
                }
                catch
                {
                    await host.DisposeAsync();
                    throw;
                }
                builtinHost = host;
                hosts.Add(host);
                if (!string.IsNullOrWhiteSpace(host.ServerInstructions))
                    serverInstructions.Add(host.ServerInstructions!.Trim());
            }

            // 2) 外部 MCP：单个连接失败只降级为警告（不阻断会话启动）
            foreach (var server in servers.Where(s => s is { Builtin: false, Enabled: true }))
            {
                McpToolHost? host = null;
                try
                {
                    host = McpToolHost.Create(server);
                    await host.ConnectAsync(ct);
                    hosts.Add(host);
                    externalHosts.Add((server, host));
                    if (!string.IsNullOrWhiteSpace(host.ServerInstructions))
                        serverInstructions.Add($"[{server.Name}] {host.ServerInstructions!.Trim()}");
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested)
                        throw;
                    try { if (host is not null) await host.DisposeAsync(); } catch { /* 忽略关停失败 */ }
                    var error = Shorten(ex.Message);
                    mcpWarnings.Add($"{server.Name}: {error}");
                    infoById[server.Id] = new ExternalMcpServerInfo(server.Name, 0, Array.Empty<string>(), error);
                }
            }
        }
        catch
        {
            foreach (var h in hosts)
            {
                try { await h.DisposeAsync(); } catch { /* 忽略关停失败 */ }
            }
            throw;
        }

        // 工具装配：内置在前（工具名原样保留），外部按重名冲突加服务器前缀
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var builtinTools = new List<IAgentTool>();
        // 内部轮次通知工具：在 ToolFilter 过滤前单独捕获引用（过滤后模型不可见，但会话仍可程序化调用）
        McpClientTool? roundTool = builtinHost?.Tools.FirstOrDefault(t => t.Name == ToolFilter.InternalRoundTool);
        IAgentTool? roundNotifier = roundTool is null ? null : new McpAgentTool(roundTool);
        if (builtinHost is not null)
        {
            foreach (var tool in ToolFilter.Apply(builtinHost.Tools, config))
            {
                usedNames.Add(tool.Name);
                builtinTools.Add(new McpAgentTool(tool, config.CompactToolDescriptions));
            }
        }

        // Graph-RAG：仅在内置 topology 工具存在时启用；每次发送前自动解析资产并拉 1 跳邻域。
        var graphContext = AssetGraphContextProvider.Create(
            builtinTools.FirstOrDefault(t => string.Equals(t.Name, "topology_search", StringComparison.Ordinal)),
            builtinTools.FirstOrDefault(t => string.Equals(t.Name, "topology_neighbors", StringComparison.Ordinal)),
            assetGraph);

        var mcpTools = new List<IAgentTool>(builtinTools);
        foreach (var (server, host) in externalHosts)
        {
            var renamed = new List<string>();
            var count = 0;
            foreach (var tool in ToolFilter.Apply(host.Tools, config))
            {
                var name = tool.Name;
                if (!usedNames.Add(name))
                {
                    name = PrefixToolName(server.Id, tool.Name, usedNames);
                    renamed.Add($"`{name}` → 原名 `{tool.Name}`");
                }
                count++;
                mcpTools.Add(new McpAgentTool(tool, config.CompactToolDescriptions, name));
            }
            infoById[server.Id] = new ExternalMcpServerInfo(server.Name, count, renamed, null);
        }

        // 外部 MCP 摘要按配置顺序（含连接失败的），供系统提示渲染
        var externalInfos = servers
            .Where(s => s is { Builtin: false, Enabled: true })
            .Select(s => infoById.TryGetValue(s.Id, out var info) ? info : null)
            .Where(info => info is not null)
            .Select(info => info!)
            .ToList();

        // 本次会话实际暴露的**内置** MCP 工具域（经服务端分组 + Agent 只读/分组 两层过滤后的交集），注入系统提示做"能力边界"护栏；
        // 外部 MCP 工具不计入（其分组由第三方自定，且可能被重名改写）。
        var availableGroups = builtinTools
            .Select(t => t.ToolGroup)
            .Where(g => g is not null)
            .Select(g => g!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var serverInstructionsText = serverInstructions.Count == 0 ? null : string.Join("\n\n", serverInstructions);

        // 两层工作区（需求 D）：共享工作区恒在；会话分配独立目录时主根=会话目录（产出落会话），
        // 共享根经 WorkspaceTools 的 shared/ 前缀继续可达（资产档案/脚本库不被割裂）。
        var sharedRoot = WorkspaceTools.ResolveDir(config.WorkspaceDir);
        var sessionRoot = string.IsNullOrWhiteSpace(sessionWorkspaceDir)
            ? null
            : Path.GetFullPath(sessionWorkspaceDir);
        var workspaceRoot = sessionRoot ?? sharedRoot;
        var isAdmin = string.Equals(ownerRole, HostEnv.AdminRole, StringComparison.OrdinalIgnoreCase);
        var sharedWritable = ownerRole is null || isAdmin;
        var workspaceTools = WorkspaceTools.Create(workspaceRoot, sessionRoot is null ? null : sharedRoot,
            sharedWritable).ToList();
        var skillsDir = SkillRegistry.ResolveDir(config.SkillsDir, bundledSkillsDir);
        var skillTools = skillsDir is null ? new List<IAgentTool>() : SkillTools.Create(skillsDir).ToList();

        var spill = TryCreateSpill(config, ownerUserId, scopeSpillByOwner);
        var spillTools = spill is null ? new List<IAgentTool>() : SpillTools.Create(spill).ToList();

        // 记忆先于工具装配：memory != null 时注册 ops_memory_*（否则不暴露，提示词同步不注入该段）
        var (memory, memoryDisabledReason) = await TryCreateMemoryAsync(config, provider, ownerUserId,
            scopeMemoryByOwner, isAdmin, ct);
        var memoryTools = memory is null ? new List<IAgentTool>() : MemoryTools.Create(memory).ToList();
        // 脚本复用登记（写 MANIFEST + 可选同步记忆）：台账恒在共享区（D6 scripts/ 留共享，不随会话割裂）
        var scriptTools = ScriptTools.Create(sharedRoot, memory, sharedWritable).ToList();
        Func<string, IReadOnlyCollection<string>?, CancellationToken, Task<string?>>? recall =
            memory is null
                ? null
                : (query, assetIds, token) => memory.RecallAsync(query, assetIds: assetIds, ct: token);

        // 系统提示按"激活技能"动态构建（未选技能=仅注入索引）；切换技能时用同一定义重算。
        var skillFiles = skillsDir is null ? null : SkillRegistry.ListReferenceFiles(skillsDir);
        Func<IEnumerable<string>?, AgentPromptContext?, string> promptBuilder = (activeSkills, agent) => SystemPromptBuilder.Build(
            serverInstructionsText,
            SkillRegistry.Load(config.SkillsDir, bundledSkillsDir, availableGroups, activeSkills?.ToList()),
            config.SystemPrompt,
            skillFiles: skillFiles,
            spillTools: spill is not null,
            subAgent: config.EnableSubAgent,
            responseStyle: config.ResponseStyle,
            availableGroups: availableGroups,
            agentName: agent?.Name,
            agentPrompt: agent?.SystemPrompt,
            agentStyle: agent?.ResponseStyle,
            skillsRoot: skillsDir,
            externalMcp: externalInfos,
            builtinMcpDisabled: builtin is not { Enabled: true },   // 全局停用或本会话子集排除（D8 例外：会话级整体关闭）
            memoryTools: memory is not null,
            scriptTools: scriptTools.Count > 0,
            assetGraph: availableGroups.Contains(ToolGroups.Topology) ? assetGraph : null,
            sessionWorkspaceDir: sessionRoot,
            sharedWorkspaceDir: sessionRoot is null ? null : sharedRoot);

        // 主代理基础工具 = MCP + 工作区文档 + 技能参考 + 落盘读取 + 记忆/脚本登记 + 宿主注入的本地工具（不含子代理/计划，它们依赖 client/回调，按会话构建）
        var baseTools = mcpTools.Concat(workspaceTools).Concat(skillTools).Concat(spillTools)
            .Concat(memoryTools).Concat(scriptTools)
            .Concat(extraTools ?? Enumerable.Empty<IAgentTool>()).ToList();
        // 子代理只给只读类工具(MCP/技能/落盘)，不给工作区写工具与计划, 避免副作用与递归
        var subTools = mcpTools.Concat(skillTools).Concat(spillTools).ToList();
        // 全局只读：MCP 工具已由 ToolFilter 过滤，这里再过滤本地工具（写文档工具等），保证运行时工具集一次性恒定。
        if (config.ReadOnly)
        {
            baseTools = baseTools.Where(t => t.ReadOnly).ToList();
            subTools = subTools.Where(t => t.ReadOnly).ToList();
        }

        // 连接器状态快照：内置 + 启用的外部（按配置顺序），供会话窗口「连接器」列表展示工具数/错误
        var hostStatuses = new List<McpHostStatus>();
        if (builtin is { Enabled: true } && builtinHost is not null)
        {
            var name = string.IsNullOrWhiteSpace(builtin.Name) ? "LitSSH MCP" : builtin.Name;
            hostStatuses.Add(new McpHostStatus(builtin.Id, name, true, true, builtinTools.Count, null));
        }
        foreach (var s in servers.Where(x => x is { Builtin: false, Enabled: true }))
        {
            if (infoById.TryGetValue(s.Id, out var info))
                hostStatuses.Add(new McpHostStatus(s.Id, s.Name, false, info.Error is null, info.ToolCount, info.Error));
        }

        var runtime = new AgentRuntime(hosts, mcpWarnings, hostStatuses, provider, memory, memoryDisabledReason, config, promptBuilder, activeAgent, baseTools, subTools, roundNotifier, onPlan, recall, graphContext, spill, history);
        runtime.WorkspaceRoot = workspaceRoot;
        if (activeSkillIds is { Count: > 0 })
            runtime.SetActiveSkills(activeSkillIds);
        if (planMode)
            runtime.SetPlanMode(true);
        runtime.StartWorkspaceIndexing(workspaceRoot);
        if (sessionRoot is not null)
            runtime.StartWorkspaceIndexing(sharedRoot);   // 会话模式下共享区资产索引不丢
        return runtime;
    }

    private static SpillStore? TryCreateSpill(AgentConfig config, string? ownerUserId, bool scopeByOwner)
    {
        if (!config.SpillLargeToolResults)
            return null;
        try
        {
            var dir = string.IsNullOrWhiteSpace(config.SpillDir)
                ? Path.Combine(ConfigPaths.AppDataDir, "spills")
                : config.SpillDir;
            return new SpillStore(dir, config.SpillRetentionDays,
                scopeByOwner ? (string.IsNullOrWhiteSpace(ownerUserId) ? "local" : ownerUserId) : null);
        }
        catch
        {
            return null;   // 目录不可用则退回截断
        }
    }

    /// <summary>把一轮对话（用户+助手）加入长期记忆（无记忆时为空操作；内容经质量门压缩，同源保留上限）。</summary>
    public async Task IndexTurnAsync(string userText, string assistantText, CancellationToken ct = default)
    {
        if (Memory is null)
            return;
        try
        {
            var content = MemoryQuality.BuildRawContent(userText, assistantText);
            if (content is null)
                return;
            await Memory.IndexAsync("会话", content, ct);
            await Memory.PruneRawAsync("会话", RawPerSourceLimit, ct);
        }
        catch
        {
            // 记忆写入失败不影响主流程
        }
    }

    /// <summary>同源("会话")raw 片段保留条数上限。</summary>
    public const int RawPerSourceLimit = 200;

    /// <summary>
    /// 尝试创建长期记忆服务。返回 (服务, 禁用原因)：
    /// 未启用 → (null, null)；已启用但配置不全/初始化失败 → (null, 原因)，由 UI 展示（不再静默）。
    /// </summary>
    private static async Task<(AgentMemoryService? Memory, string? Reason)> TryCreateMemoryAsync(
        AgentConfig config, AgentProviderConfig provider, string? ownerUserId, bool scopeByOwner, bool isAdmin,
        CancellationToken ct)
    {
        if (config.Memory?.Enabled != true)
            return (null, null);
        if (string.IsNullOrWhiteSpace(config.Memory.Model))
            return (null, "已启用但未填写 Embeddings 模型名");
        if (scopeByOwner && string.IsNullOrWhiteSpace(ownerUserId))
            return (null, "无法确定当前用户，已禁用长期记忆");
        try
        {
            var embedder = EmbeddingClientFactory.Create(config.Memory, provider.ApiKey);
            var access = scopeByOwner ? new MemoryAccessScope(ownerUserId!, isAdmin) : null;
            var memory = new AgentMemoryService(new AgentMemoryStore(null, access), embedder, config.Memory.TopK,
                config.Memory.RecallThreshold);
            await memory.InitializeAsync(ct);
            return (memory, null);
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length <= 120 ? ex.Message : ex.Message[..120] + "…";
            return (null, "Embeddings 初始化失败: " + msg);
        }
    }

    private void StartWorkspaceIndexing(string workspaceDir)
    {
        if (Memory is null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!Directory.Exists(workspaceDir))
                    return;

                foreach (var file in Directory.EnumerateFiles(workspaceDir, "*", SearchOption.AllDirectories).Take(50))
                {
                    var info = new FileInfo(file);
                    if (info.Length > 200_000)
                        continue;
                    var content = await File.ReadAllTextAsync(file);
                    await Memory.IndexAsync($"文档:{Path.GetRelativePath(workspaceDir, file)}", content);
                }
            }
            catch
            {
                // 后台索引失败忽略
            }
        });
    }

    /// <summary>外部工具与既有工具重名时加服务器前缀（冲突则再加序号），并登记到已用名集合。</summary>
    private static string PrefixToolName(string serverId, string toolName, HashSet<string> used)
    {
        var prefix = string.IsNullOrWhiteSpace(serverId) ? "mcp" : serverId.Trim();
        var candidate = $"{prefix}_{toolName}";
        for (var i = 2; used.Contains(candidate); i++)
            candidate = $"{prefix}_{toolName}_{i}";
        used.Add(candidate);
        return candidate;
    }

    private static string Shorten(string? message)
    {
        var text = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length == 0)
            text = "连接失败";
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in Hosts)
        {
            try { await host.DisposeAsync(); } catch { /* 关停子进程失败忽略 */ }
        }
    }
}
