using System.IO;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Agent;
using LitSSHmcp.Agent.Scheduling;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.Services.AiTasks;

/// <summary>一次 AI 定时任务运行的结果。</summary>
public sealed class AiTaskRunResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string? Error { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }

    public string NotificationText =>
        !string.IsNullOrWhiteSpace(Error) ? Error! : Summary;
}

/// <summary>
/// 桌面端无界面 Agent 运行器：直接创建会话 + 装配 AgentRuntime + 发送指令，
/// 不依赖 AgentViewModel/UI，不干扰用户当前正在看的会话。
/// 默认只读，避免无人值守下的写操作审批问题。
/// </summary>
public sealed class HeadlessAgentRunner
{
    private readonly IConfigService _configService;
    private readonly IContextStore _store;
    private readonly string _bundledSkillsDir;

    public HeadlessAgentRunner(IConfigService configService, IContextStore store, string bundledSkillsDir)
    {
        _configService = configService;
        _store = store;
        _bundledSkillsDir = bundledSkillsDir;
    }

    public async Task<AiTaskRunResult> RunAsync(AiTaskConfig task, AiTaskTrigger trigger, CancellationToken ct = default)
    {
        AppConfig config;
        try
        {
            config = await _configService.LoadConfigAsync();
        }
        catch (Exception ex)
        {
            return new AiTaskRunResult { Success = false, Error = "读取配置失败: " + ex.Message };
        }

        var agentConfig = Clone(config.Agent);
        agentConfig.ReadOnly = task.ReadOnly;
        if (task.ReadOnly)
            agentConfig.Profile = AgentToolProfile.Diagnose;
        if (task.MaxTurns > 0)
            agentConfig.MaxToolIterations = Math.Clamp(task.MaxTurns, 1, 100);
        if (task.TimeoutSeconds > 0)
        {
            agentConfig.Budget ??= new AgentBudgetConfig();
            agentConfig.Budget.MaxTurnSeconds = task.TimeoutSeconds;
        }

        var provider = ResolveProvider(agentConfig, task.ProviderId);
        if (provider is null)
            return new AiTaskRunResult { Success = false, Error = "未配置可用的 AI 模型。" };

        if (task.TokenBudget > 0)
        {
            var price = Math.Max(provider.InputPricePerMTok, provider.OutputPricePerMTok);
            if (price > 0)
            {
                agentConfig.Budget ??= new AgentBudgetConfig();
                agentConfig.Budget.MaxSessionCost = task.TokenBudget / 1_000_000.0 * price;
            }
        }

        var owner = string.IsNullOrWhiteSpace(task.OwnerUserId) ? "local" : task.OwnerUserId;
        long sessionId;
        try
        {
            sessionId = await _store.CreateSessionAsync("[定时] " + task.Name, ct);
            await _store.SetSessionOwnerUserIdAsync(sessionId, owner, ct);
            var workspaceRoot = WorkspaceTools.ResolveDir(agentConfig.WorkspaceDir);
            var workspaceDir = WorkspaceLayout.ResolveSessionDir(workspaceRoot, owner, sessionId);
            Directory.CreateDirectory(workspaceDir);
            await _store.SetSessionWorkspaceDirAsync(sessionId, workspaceDir, ct);
            await _store.AppendMessageAsync(sessionId, "user", task.Instruction, ct);

            var activeAgent = agentConfig.Agents?
                .FirstOrDefault(a => string.Equals(a.Id, task.AgentId, StringComparison.OrdinalIgnoreCase))
                ?.ToPromptContext();

            await using var runtime = await AgentRuntime.StartAsync(
                agentConfig,
                provider,
                _bundledSkillsDir,
                history: null,
                activeAgent: activeAgent,
                sessionWorkspaceDir: workspaceDir,
                ownerUserId: owner,
                sessionId: sessionId,
                enableWorkLog: false,
                scopeSpillByOwner: false,
                scopeMemoryByOwner: false,
                ct: ct);

            var text = new StringBuilder();
            string? error = null;
            var progress = new SyncProgress(e =>
            {
                if (e.Kind == AgentEventKind.AssistantText && e.Text is not null)
                    text.Append(e.Text);
                if (e.Kind == AgentEventKind.Error)
                    error = string.IsNullOrWhiteSpace(e.Text) ? "AI 运行失败" : e.Text;
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (task.TimeoutSeconds > 0)
                timeout.CancelAfter(TimeSpan.FromSeconds(task.TimeoutSeconds));

            using (LocalAudit.BeginRunScope(sessionId.ToString()))
            {
                await runtime.Session.SendAsync(
                    new AIContent[] { new TextContent(task.Instruction) },
                    progress,
                    timeout.Token);
            }

            var assistantText = text.ToString().Trim();
            if (assistantText.Length > 0)
                await _store.AppendMessageAsync(sessionId, "assistant", assistantText, CancellationToken.None);

            var inputTokens = runtime.Session.SessionInputTokens;
            var outputTokens = runtime.Session.SessionOutputTokens;
            var totalTokens = inputTokens + outputTokens;
            var budgetExceeded = task.TokenBudget > 0 && totalTokens > task.TokenBudget;

            if (budgetExceeded)
            {
                return new AiTaskRunResult
                {
                    Success = false,
                    SessionId = sessionId.ToString(),
                    Error = $"运行已超出 token 预算（{totalTokens} > {task.TokenBudget}）。",
                    Summary = Truncate(assistantText, 4000),
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens
                };
            }

            if (error is not null)
            {
                return new AiTaskRunResult
                {
                    Success = false,
                    SessionId = sessionId.ToString(),
                    Error = error,
                    Summary = Truncate(assistantText, 4000),
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens
                };
            }

            return new AiTaskRunResult
            {
                Success = true,
                SessionId = sessionId.ToString(),
                Summary = string.IsNullOrWhiteSpace(assistantText) ? "（本轮无输出）" : Truncate(assistantText, 4000),
                InputTokens = inputTokens,
                OutputTokens = outputTokens
            };
        }
        catch (OperationCanceledException)
        {
            return new AiTaskRunResult
            {
                Success = false,
                Cancelled = true,
                Error = ct.IsCancellationRequested ? "已取消" : $"运行超时（{task.TimeoutSeconds} 秒）"
            };
        }
        catch (Exception ex)
        {
            return new AiTaskRunResult
            {
                Success = false,
                Error = "运行失败: " + ex.Message
            };
        }
    }

    private static AgentProviderConfig? ResolveProvider(AgentConfig agent, string? providerId)
    {
        var flat = AgentProviderGroupConfig.Flatten(agent.Providers);
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var selected = flat.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
                return selected;
        }
        return flat.FirstOrDefault(p => string.Equals(p.Id, agent.ActiveProviderId, StringComparison.OrdinalIgnoreCase))
               ?? flat.FirstOrDefault();
    }

    private static AgentConfig Clone(AgentConfig source) =>
        JsonSerializer.Deserialize<AgentConfig>(JsonSerializer.Serialize(source)) ?? new AgentConfig();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    /// <summary>同步转发事件，保证文本拼接顺序。</summary>
    private sealed class SyncProgress(Action<AgentEvent> handler) : IProgress<AgentEvent>
    {
        public void Report(AgentEvent value) => handler(value);
    }
}
