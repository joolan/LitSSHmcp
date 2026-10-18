using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Web.Sessions;

/// <summary>一次运行时装配请求（宿主已解析好归属/工作目录/历史/摘要）。</summary>
public sealed record WebRuntimeRequest(
    long SessionId,
    string OwnerUserId,
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
/// 不注入本机终端/提升工具（桌面专属，需桌面确认）；连接器子集暂继承全局（M3-4 授权矩阵接管收窄）。
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
            ownerUserId: request.OwnerUserId);
        if (!string.IsNullOrEmpty(request.Summary))
            runtime.Session.RestoreSummary(request.Summary);
        return new AgentWebRuntime(runtime);
    }

    private sealed class AgentWebRuntime(AgentRuntime runtime) : IWebRuntime
    {
        public string Summary => runtime.Session.Summary;

        public Task SendAsync(string userText, IProgress<AgentEvent>? progress, CancellationToken ct)
            => runtime.Session.SendAsync(userText, progress, ct);

        public async ValueTask DisposeAsync() => await runtime.DisposeAsync();
    }
}
