using System.Collections.Concurrent;

namespace LitSSHmcp.Web;

/// <summary>
/// 限速策略（评估报告 §10.B.1"登录限速" / §10.B.6"登录与 API 层限速"，防内网刷接口与口令喷洒）。
/// 按客户端 IP 分桶、固定 1 分钟窗口；登录与其余 API 各一桶。测试可替换本实例改小阈值。
/// </summary>
public sealed class WebRateLimitPolicy
{
    /// <summary>每 IP 每分钟允许的 /api/login 次数（账号级另有失败锁定 WebAuthPolicy 兜底）。</summary>
    public int LoginPerMinute { get; set; } = 30;

    /// <summary>每 IP 每分钟允许的其余 /api/* 请求次数（WS 不经 HTTP 限速，另有 owner/Origin 校验）。</summary>
    public int ApiPerMinute { get; set; } = 600;
}

/// <summary>
/// 限速计数器（进程内内存态）：键 = IP|桶|分钟序号，固定窗口计数；过期键在增长时顺带清理。
/// 可注入时间点便于测试窗口翻转。
/// </summary>
public sealed class WebRateLimitStore
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private int _lastPruneMinute = -1;

    /// <summary>尝试记账一次；false = 该窗口内已超限（调用方应返回 429）。</summary>
    public bool TryAcquire(string ip, string bucket, int limit, DateTimeOffset now)
    {
        if (limit <= 0)
            return false;
        var minute = (int)(now.ToUnixTimeSeconds() / 60);
        Prune(minute);
        var key = string.Concat(ip, "|", bucket, "|", minute.ToString());
        var count = _counts.AddOrUpdate(key, 1, (_, v) => v + 1);
        return count <= limit;
    }

    /// <summary>测试用：清空全部计数。</summary>
    public void Reset() => _counts.Clear();

    private void Prune(int currentMinute)
    {
        if (currentMinute == _lastPruneMinute || _counts.IsEmpty)
            return;
        if (Interlocked.Exchange(ref _lastPruneMinute, currentMinute) == currentMinute)
            return;
        foreach (var key in _counts.Keys)
        {
            var i = key.LastIndexOf('|');
            if (i >= 0 && int.TryParse(key[(i + 1)..], out var m) && m < currentMinute)
                _counts.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// HTTP 层限速中间件：/api/login 与 /api/* 分桶计数，超限 429 + 落 rate_limited 访问日志（§10.A.3）。
/// 静态资源与 /health 不限速（登录页与健康检查可用）；WS 握手不走此中间件。
/// </summary>
public sealed class WebRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly WebRateLimitPolicy _policy;
    private readonly WebRateLimitStore _store;
    private readonly WebAccessLog _accessLog;

    public WebRateLimitMiddleware(RequestDelegate next, WebRateLimitPolicy policy,
        WebRateLimitStore store, WebAccessLog accessLog)
    {
        _next = next;
        _policy = policy;
        _store = store;
        _accessLog = accessLog;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            var isLogin = path.StartsWith("/api/login", StringComparison.Ordinal);
            var bucket = isLogin ? "login" : "api";
            var limit = isLogin ? _policy.LoginPerMinute : _policy.ApiPerMinute;
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_store.TryAcquire(ip, bucket, limit, DateTimeOffset.UtcNow))
            {
                _accessLog.Write("rate_limited", context.GetWebPrincipal()?.Username, ip, bucket);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new { error = "请求过于频繁，请稍后再试" });
                return;
            }
        }
        await _next(context);
    }
}
