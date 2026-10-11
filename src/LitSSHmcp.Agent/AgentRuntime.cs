using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 一次运行中的 AI 运维助手：持有 MCP 工具宿主 + 对话会话 + 可选的长期记忆。调用方负责在切换配置/关闭时 Dispose。
/// **MCP 宿主与工具装配只做一次**；切换会话/切换模型用 <see cref="ResetSession"/> 仅重建对话会话，不重连 MCP。
/// </summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    public McpToolHost Host { get; }
    public AgentSession Session { get; private set; }
    public AgentProviderConfig Provider { get; private set; }
    public AgentMemoryService? Memory { get; }
    public int ToolCount => Session.ToolCount;

    /// <summary>本次会话实际暴露的 MCP 工具能力域（供 UI 技能选择器判定可用性）。</summary>
    public IReadOnlySet<string> AvailableGroups =>
        _baseTools.Select(t => t.ToolGroup).Where(g => g is not null).Select(g => g!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly AgentConfig _config;
    private string _prompt;
    private readonly Func<IEnumerable<string>?, string> _promptBuilder;
    private readonly string? _readOnlyNote;
    private readonly List<IAgentTool> _baseTools;
    private readonly List<IAgentTool> _subTools;
    private readonly Action<IReadOnlyList<PlanItem>>? _onPlan;
    private readonly Func<string, CancellationToken, Task<string?>>? _recall;
    private readonly SpillStore? _spill;
    private List<string> _activeSkillIds = new();
    private bool _planMode;
    private IReadOnlyDictionary<string, AgentToolPolicy>? _toolPolicy;

    private const string ReadOnlyNote =
        "\n\n【只读模式（全局配置）】仅可调用只读工具；禁止任何写操作（命令/SQL/文件写入/重启等）。"
        + "如用户要求写操作，说明当前处于只读模式并给出替代建议。";

    private AgentRuntime(
        McpToolHost host,
        AgentProviderConfig provider,
        AgentMemoryService? memory,
        AgentConfig config,
        Func<IEnumerable<string>?, string> promptBuilder,
        List<IAgentTool> baseTools,
        List<IAgentTool> subTools,
        Action<IReadOnlyList<PlanItem>>? onPlan,
        Func<string, CancellationToken, Task<string?>>? recall,
        SpillStore? spill,
        IEnumerable<ChatMessage>? history)
    {
        Host = host;
        Provider = provider;
        Memory = memory;
        _config = config;
        _readOnlyNote = config.ReadOnly ? ReadOnlyNote : null;
        _promptBuilder = promptBuilder;
        _prompt = ApplyReadOnlyNote(promptBuilder(null));
        _baseTools = baseTools;
        _subTools = subTools;
        _onPlan = onPlan;
        _recall = recall;
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
        ApplyReadOnlyNote(_promptBuilder(_activeSkillIds.Count == 0 ? null : _activeSkillIds));

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
        if (_config.EnableSubAgent)
            tools.Add(SubAgents.Create(client, provider, _config, _subTools, _readOnlyNote));
        if (_onPlan is not null)
            tools.Add(PlanTools.Create(_onPlan));
        var session = new AgentSession(client, provider, tools, _prompt, _config, history, _recall, _spill);
        session.SetToolPolicy(_toolPolicy);
        session.SetPlanMode(_planMode);
        return session;
    }

    /// <summary>连接 MCP 服务器、装配工具与系统提示、构建对话会话（含可选长期记忆）。</summary>
    public static async Task<AgentRuntime> StartAsync(
        AgentConfig config,
        AgentProviderConfig provider,
        string? bundledSkillsDir,
        IEnumerable<ChatMessage>? history = null,
        Action<IReadOnlyList<PlanItem>>? onPlan = null,
        IReadOnlyCollection<string>? activeSkillIds = null,
        bool planMode = false,
        CancellationToken ct = default)
    {
        var launch = AgentPaths.ResolveLaunch(config.McpServerPath)
            ?? throw new InvalidOperationException(
                "未找到 MCP 服务器(LitSSHmcp.McpServer.exe): 请将服务器放到 App 的 mcp/ 目录, 或在「AI 助手设置」中指定路径。");

        var host = new McpToolHost(launch);
        try
        {
            await host.ConnectAsync(ct);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        var mcpTools = ToolFilter.Apply(host.Tools, config)
            .Select(t => (IAgentTool)new McpAgentTool(t, config.CompactToolDescriptions)).ToList();
        // 本次会话实际暴露的 MCP 工具域（经服务端分组 + Agent 只读/分组 两层过滤后的交集），注入系统提示做"能力边界"护栏。
        var availableGroups = mcpTools
            .Select(t => t.ToolGroup)
            .Where(g => g is not null)
            .Select(g => g!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workspaceTools = WorkspaceTools.Create(WorkspaceTools.ResolveDir(config.WorkspaceDir)).ToList();
        var skillsDir = SkillRegistry.ResolveDir(config.SkillsDir, bundledSkillsDir);
        var skillTools = skillsDir is null ? new List<IAgentTool>() : SkillTools.Create(skillsDir).ToList();

        var spill = TryCreateSpill(config);
        var spillTools = spill is null ? new List<IAgentTool>() : SpillTools.Create(spill).ToList();

        // 系统提示按"激活技能"动态构建（未选技能=仅注入索引）；切换技能时用同一定义重算。
        var skillFiles = skillsDir is null ? null : SkillRegistry.ListReferenceFiles(skillsDir);
        Func<IEnumerable<string>?, string> promptBuilder = activeSkills => SystemPromptBuilder.Build(
            host.ServerInstructions,
            SkillRegistry.Load(config.SkillsDir, bundledSkillsDir, availableGroups, activeSkills?.ToList()),
            config.SystemPrompt,
            skillFiles: skillFiles,
            spillTools: spill is not null,
            subAgent: config.EnableSubAgent,
            responseStyle: config.ResponseStyle,
            availableGroups: availableGroups);

        var memory = await TryCreateMemoryAsync(config, provider, ct);
        Func<string, CancellationToken, Task<string?>>? recall =
            memory is null ? null : (query, token) => memory.RecallAsync(query, token);

        // 主代理基础工具 = MCP + 工作区文档 + 技能参考 + 落盘读取（不含子代理/计划，它们依赖 client/回调，按会话构建）
        var baseTools = mcpTools.Concat(workspaceTools).Concat(skillTools).Concat(spillTools).ToList();
        // 子代理只给只读类工具(MCP/技能/落盘)，不给工作区写工具与计划, 避免副作用与递归
        var subTools = mcpTools.Concat(skillTools).Concat(spillTools).ToList();
        // 全局只读：MCP 工具已由 ToolFilter 过滤，这里再过滤本地工具（写文档工具等），保证运行时工具集一次性恒定。
        if (config.ReadOnly)
        {
            baseTools = baseTools.Where(t => t.ReadOnly).ToList();
            subTools = subTools.Where(t => t.ReadOnly).ToList();
        }

        var runtime = new AgentRuntime(host, provider, memory, config, promptBuilder, baseTools, subTools, onPlan, recall, spill, history);
        if (activeSkillIds is { Count: > 0 })
            runtime.SetActiveSkills(activeSkillIds);
        if (planMode)
            runtime.SetPlanMode(true);
        runtime.StartWorkspaceIndexing(WorkspaceTools.ResolveDir(config.WorkspaceDir));
        return runtime;
    }

    private static SpillStore? TryCreateSpill(AgentConfig config)
    {
        if (!config.SpillLargeToolResults)
            return null;
        try
        {
            var dir = string.IsNullOrWhiteSpace(config.SpillDir)
                ? Path.Combine(ConfigPaths.AppDataDir, "spills")
                : config.SpillDir;
            return new SpillStore(dir, config.SpillRetentionDays);
        }
        catch
        {
            return null;   // 目录不可用则退回截断
        }
    }

    /// <summary>把一轮对话（用户+助手）加入长期记忆（无记忆时为空操作）。</summary>
    public async Task IndexTurnAsync(string userText, string assistantText, CancellationToken ct = default)
    {
        if (Memory is null)
            return;
        try
        {
            await Memory.IndexAsync("会话", $"用户: {userText}\n助手: {assistantText}", ct);
        }
        catch
        {
            // 记忆写入失败不影响主流程
        }
    }

    private static async Task<AgentMemoryService?> TryCreateMemoryAsync(AgentConfig config, AgentProviderConfig provider, CancellationToken ct)
    {
        if (config.Memory?.Enabled != true)
            return null;
        try
        {
            var embedder = EmbeddingClientFactory.Create(config.Memory, provider.ApiKey);
            var memory = new AgentMemoryService(new AgentMemoryStore(), embedder, config.Memory.TopK);
            await memory.InitializeAsync(ct);
            return memory;
        }
        catch
        {
            return null;   // 配置不全/端点不可用则静默禁用记忆
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

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
