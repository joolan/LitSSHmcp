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

    private readonly AgentConfig _config;
    private readonly string _prompt;
    private readonly List<IAgentTool> _baseTools;
    private readonly List<IAgentTool> _subTools;
    private readonly Action<IReadOnlyList<PlanItem>>? _onPlan;
    private readonly Func<string, CancellationToken, Task<string?>>? _recall;
    private readonly SpillStore? _spill;

    private AgentRuntime(
        McpToolHost host,
        AgentProviderConfig provider,
        AgentMemoryService? memory,
        AgentConfig config,
        string prompt,
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
        _prompt = prompt;
        _baseTools = baseTools;
        _subTools = subTools;
        _onPlan = onPlan;
        _recall = recall;
        _spill = spill;
        Session = BuildSession(provider, history);
    }

    /// <summary>用当前配置与工具重新构建对话会话（仅换历史/模型，不重连 MCP）。</summary>
    public void ResetSession(AgentProviderConfig provider, IEnumerable<ChatMessage>? history)
    {
        Provider = provider;
        Session = BuildSession(provider, history);
    }

    private AgentSession BuildSession(AgentProviderConfig provider, IEnumerable<ChatMessage>? history)
    {
        var client = ChatClientFactory.Create(provider);
        var tools = new List<IAgentTool>(_baseTools);
        if (_config.EnableSubAgent)
            tools.Add(SubAgents.Create(client, provider, _config, _subTools));
        if (_onPlan is not null)
            tools.Add(PlanTools.Create(_onPlan));
        return new AgentSession(client, provider, tools, _prompt, _config, history, _recall, _spill);
    }

    /// <summary>连接 MCP 服务器、装配工具与系统提示、构建对话会话（含可选长期记忆）。</summary>
    public static async Task<AgentRuntime> StartAsync(
        AgentConfig config,
        AgentProviderConfig provider,
        string? bundledSkillsDir,
        IEnumerable<ChatMessage>? history = null,
        Action<IReadOnlyList<PlanItem>>? onPlan = null,
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
        var workspaceTools = WorkspaceTools.Create(WorkspaceTools.ResolveDir(config.WorkspaceDir)).ToList();
        var skillsDir = SkillRegistry.ResolveDir(config.SkillsDir, bundledSkillsDir);
        var skillTools = skillsDir is null ? new List<IAgentTool>() : SkillTools.Create(skillsDir).ToList();

        var spill = TryCreateSpill(config);
        var spillTools = spill is null ? new List<IAgentTool>() : SpillTools.Create(spill).ToList();

        var prompt = SystemPromptBuilder.Build(
            host.ServerInstructions,
            SkillRegistry.Load(config.SkillsDir, bundledSkillsDir),
            config.SystemPrompt,
            skillFiles: skillsDir is null ? null : SkillRegistry.ListReferenceFiles(skillsDir),
            spillTools: spill is not null,
            subAgent: config.EnableSubAgent,
            responseStyle: config.ResponseStyle);

        var memory = await TryCreateMemoryAsync(config, provider, ct);
        Func<string, CancellationToken, Task<string?>>? recall =
            memory is null ? null : (query, token) => memory.RecallAsync(query, token);

        // 主代理基础工具 = MCP + 工作区文档 + 技能参考 + 落盘读取（不含子代理/计划，它们依赖 client/回调，按会话构建）
        var baseTools = mcpTools.Concat(workspaceTools).Concat(skillTools).Concat(spillTools).ToList();
        // 子代理只给只读类工具(MCP/技能/落盘)，不给工作区写工具与计划, 避免副作用与递归
        var subTools = mcpTools.Concat(skillTools).Concat(spillTools).ToList();

        var runtime = new AgentRuntime(host, provider, memory, config, prompt, baseTools, subTools, onPlan, recall, spill, history);
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
