namespace LitSSHmcp.Core.Services.DnsServer;

/// <summary>DNS 运行状态快照（UI 定时刷新）。</summary>
public sealed record DnsStatus(
    bool Running,
    string? UdpEndpoint,
    string? TcpEndpoint,
    long TotalQueries,
    long CacheHits,
    long Forwarded,
    long Blocked,
    long Failed,
    int ActiveQueries,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ExpiresAt,
    string? LastError,
    long Coalesced = 0,
    long RateLimited = 0)
{
    public static DnsStatus Stopped(string? lastError = null) =>
        new(false, null, null, 0, 0, 0, 0, 0, 0, null, null, lastError);
}

/// <summary>热更新结果：Applied=false 表示 DNS 未运行；RestartRequired 表示监听参数需重启。</summary>
public sealed record DnsRuntimeUpdateResult(
    bool Applied,
    bool RestartRequired,
    string Message);
