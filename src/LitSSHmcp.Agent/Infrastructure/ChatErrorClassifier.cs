using System.ClientModel;
using System.Net.Sockets;

namespace LitSSHmcp.Agent;

/// <summary>稳定错误分类：显式赋值，便于落库/跨版本比对与遥测聚合。</summary>
public enum AgentErrorKind
{
    Auth = 0,
    RateLimit = 1,
    Timeout = 2,
    Network = 3,
    Server = 4,
    BadRequest = 5,
    Cancelled = 6,
    Unknown = 7,
    /// <summary>会话成本/墙钟预算耗尽（harness 内部错误，非 provider 异常）。</summary>
    Budget = 8,
    /// <summary>harness 护栏主动停止（轮次/重复调用/连续失败等）。</summary>
    Guardrail = 9,
    /// <summary>模型输出被内容过滤。</summary>
    ContentFilter = 10
}

/// <summary>把大模型调用异常分类为稳定类别，便于向用户给出可操作提示与决定是否重试。</summary>
public static class ChatErrorClassifier
{
    public static AgentErrorKind Classify(Exception ex)
    {
        if (ex is OperationCanceledException)
            return AgentErrorKind.Cancelled;

        // ResilientChatClient 把内部超时 CTS 的 OCE 包装为 TimeoutException；与用户取消区分开
        if (ex is TimeoutException)
            return AgentErrorKind.Timeout;

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

    public static string Describe(Exception ex)
    {
        var kind = Classify(ex);
        // Unknown 保留原始异常摘要（UI 连接失败提示依赖它），其余稳定分类走统一映射。
        return kind == AgentErrorKind.Unknown ? "调用失败：" + Short(ex.Message) : Describe(kind);
    }

    public static string Describe(AgentErrorKind kind) => kind switch
    {
        AgentErrorKind.Auth => "认证失败：API Key 无效或无权限",
        AgentErrorKind.RateLimit => "被限流(429)：请降低频率或稍后重试",
        AgentErrorKind.Timeout => "请求超时：检查网络/端点，或调大超时",
        AgentErrorKind.Network => "网络不可达：检查 Endpoint 与网络连通性",
        AgentErrorKind.Server => "服务端错误(5xx)：稍后重试",
        AgentErrorKind.BadRequest => "请求被拒(4xx)：检查模型名/参数是否正确",
        AgentErrorKind.Cancelled => "已取消",
        AgentErrorKind.Budget => "成本/预算超限：调大预算或缩小任务范围",
        AgentErrorKind.Guardrail => "harness 护栏停止：重复调用或工具轮次达到上限",
        AgentErrorKind.ContentFilter => "模型输出被内容过滤：调整表述或换模型",
        _ => "调用失败"
    };

    private static string Short(string text) => text.Length <= 200 ? text : text[..200];
}
