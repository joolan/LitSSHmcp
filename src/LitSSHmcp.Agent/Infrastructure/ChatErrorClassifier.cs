using System.ClientModel;
using System.Net.Sockets;

namespace LitSSHmcp.Agent;

public enum AgentErrorKind
{
    Auth,
    RateLimit,
    Timeout,
    Network,
    Server,
    BadRequest,
    Cancelled,
    Unknown
}

/// <summary>把大模型调用异常分类为稳定类别，便于向用户给出可操作提示与决定是否重试。</summary>
public static class ChatErrorClassifier
{
    public static AgentErrorKind Classify(Exception ex)
    {
        if (ex is OperationCanceledException)
            return AgentErrorKind.Cancelled;

        if (ex is ClientResultException client)
            return client.Status switch
            {
                401 or 403 => AgentErrorKind.Auth,
                429 => AgentErrorKind.RateLimit,
                408 or 504 => AgentErrorKind.Timeout,
                >= 500 => AgentErrorKind.Server,
                400 or 404 or 422 => AgentErrorKind.BadRequest,
                _ => AgentErrorKind.Unknown
            };

        if (ex is HttpRequestException || ex is SocketException || ex.InnerException is SocketException)
            return AgentErrorKind.Network;

        return AgentErrorKind.Unknown;
    }

    public static bool IsTransient(Exception ex) => Classify(ex) is
        AgentErrorKind.RateLimit or AgentErrorKind.Timeout or AgentErrorKind.Network or AgentErrorKind.Server;

    public static string Describe(Exception ex) => Classify(ex) switch
    {
        AgentErrorKind.Auth => "认证失败：API Key 无效或无权限",
        AgentErrorKind.RateLimit => "被限流(429)：请降低频率或稍后重试",
        AgentErrorKind.Timeout => "请求超时：检查网络/端点，或调大超时",
        AgentErrorKind.Network => "网络不可达：检查 Endpoint 与网络连通性",
        AgentErrorKind.Server => "服务端错误(5xx)：稍后重试",
        AgentErrorKind.BadRequest => "请求被拒(4xx)：检查模型名/参数是否正确",
        AgentErrorKind.Cancelled => "已取消",
        _ => "调用失败：" + Short(ex.Message)
    };

    private static string Short(string text) => text.Length <= 200 ? text : text[..200];
}
