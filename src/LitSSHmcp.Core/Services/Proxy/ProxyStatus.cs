namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>代理运行状态快照（UI 定时刷新）。</summary>
public sealed record ProxyStatus(
    bool Running,
    string? HttpEndpoint,
    string? Socks5Endpoint,
    int ActiveConnections,
    long TotalRequests,
    long BytesIn,
    long BytesOut,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ExpiresAt,
    string? LastError)
{
    public static ProxyStatus Stopped(string? lastError = null) =>
        new(false, null, null, 0, 0, 0, 0, null, null, lastError);
}
