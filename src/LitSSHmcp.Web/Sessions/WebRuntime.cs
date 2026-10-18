using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Web.Sessions;

/// <summary>一次运行时装配请求（宿主已解析好归属/工作目录/历史/摘要）。</summary>
public sealed record WebRuntimeRequest(
    long SessionId,
    string OwnerUserId,
    string OwnerUsername,
    string WorkspaceDir,
    IReadOnlyList<ChatMessage> History,
    string Summary);

/// <summary>Web 会话运行时抽象：把 AgentRuntime 池化细节与宿主逻辑解耦（测试可注入假运行时）。</summary>
public interface IWebRuntime : IAsyncDisposable
{
    Task SendAsync(string userText, IProgress<AgentEvent>? progress, CancellationToken ct);
    string Summary { get; }
}

public interface IWebRuntimeFactory
{
    Task<IWebRuntime> CreateAsync(WebRuntimeRequest request, CancellationToken ct);
}

/// <summary>
/// 生产实现：按会话装配 AgentRuntime（连接 MCP 子进程、烧定工作目录/归因、恢复摘要）。
/// 不注入本机终端/提升工具（桌面专属，需桌面确认）；连接器按 §10.B.7 白名单收窄（M3-4）。
/// </summary>
public sealed class AgentWebRuntimeFactory : IWebRuntimeFactory
{
    private readonly IConfigService _configService;

    public AgentWebRuntimeFactory(IConfigService configService) => _configService = configService;

    public async Task<IWebRuntime> CreateAsync(WebRuntimeRequest request, CancellationToken ct)
    {
        var config = await _configService.LoadConfigAsync();
        var agent = config.Agent;
        var flat = AgentProviderGroupConfig.Flatten(agent.Providers);
        var provider = flat.FirstOrDefault(p => p.Id == agent.ActiveProviderId) ?? flat.FirstOrDefault()
            ?? throw new InvalidOperationException("未配置可用的模型（AI 助手设置 → 模型）。");

        var runtime = await AgentRuntime.StartAsync(
            agent, provider, bundledSkillsDir: null,
            history: request.History,
            sessionWorkspaceDir: request.WorkspaceDir,
            ownerUserId: request.OwnerUserId,
            enabledMcpIds: ComputeWebEnabledMcpIds(config.Security?.Authorization ?? new AuthorizationConfig()),
            ownerUsername: request.OwnerUsername);
        if (!string.IsNullOrEmpty(request.Summary))
            runtime.Session.RestoreSummary(request.Summary);
        return new AgentWebRuntime(runtime, agent.Budget, provider.InputPricePerMTok, provider.OutputPricePerMTok);
    }

    /// <summary>
    /// Web 会话的连接器启用集合（§10.B.7 攻击面收敛）：内置 litssh 恒含（走 LitSSH 审批/授权矩阵），
    /// 外部 MCP 默认不下发 Web——仅 <c>security.authorization.webConnectors</c> 白名单内的 Id 才下发；
    /// 全局停用的连接器仍出局（子集只在全局启用集合内收窄，D8 配置层优先）。
    /// </summary>
    public static IReadOnlyCollection<string> ComputeWebEnabledMcpIds(AuthorizationConfig authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var ids = new HashSet<string>(StringComparer.Ordinal) { AgentMcpServerConfig.BuiltinDefault().Id };
        foreach (var id in authorization.WebConnectors ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id.Trim());
        }
        return ids;
    }

    private sealed class AgentWebRuntime(AgentRuntime runtime, AgentBudgetConfig? budget,
        double inputPricePerMTok, double outputPricePerMTok) : IWebRuntime
    {
        public string Summary => runtime.Session.Summary;

        public Task SendAsync(string userText, IProgress<AgentEvent>? progress, CancellationToken ct)
        {
            // 成本预算（§10.B.6）：达到 agent.budget.maxSessionCost 上限阻止新发送（§10.B.6 按用户生效）
            var block = WebBudgetGuard.ComputeCostBlock(budget,
                runtime.Session.SessionInputTokens, runtime.Session.SessionOutputTokens,
                inputPricePerMTok, outputPricePerMTok);
            if (block is not null)
                throw new InvalidOperationException(block);
            return runtime.Session.SendAsync(userText, progress, ct);
        }

        public async ValueTask DisposeAsync() => await runtime.DisposeAsync();
    }
}
