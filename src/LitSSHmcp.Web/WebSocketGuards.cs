namespace LitSSHmcp.Web;

/// <summary>
/// 进程内 WebSocket 连接数限制：按用户 / IP 计数，握手成功后持有租约，连接结束 Dispose 释放。
/// 计数与释放都在锁内完成，避免并发握手把上限撑破。
/// </summary>
public sealed class WebSocketConnectionLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>尝试占用一个连接名额；<paramref name="limit"/>&lt;=0 或 key 为空时拒绝（fail-closed）。</summary>
    public IDisposable? TryAcquire(string? key, int limit)
    {
        if (string.IsNullOrWhiteSpace(key) || limit <= 0)
            return null;

        lock (_gate)
        {
            _counts.TryGetValue(key, out var current);
            if (current >= limit)
                return null;

            _counts[key] = current + 1;
            return new Lease(this, key);
        }
    }

    private void Release(string key)
    {
        lock (_gate)
        {
            if (!_counts.TryGetValue(key, out var current))
                return;
            if (current <= 1)
                _counts.Remove(key);
            else
                _counts[key] = current - 1;
        }
    }

    private sealed class Lease(WebSocketConnectionLimiter owner, string key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(key);
        }
    }
}

/// <summary>
/// WebSocket 握手 Origin 校验：浏览器握手必须带 Origin，且 scheme / host / port 与当前请求完全一致。
/// 仅比较 Host 会让不同端口或 http/https 混用通过；缺失 Origin 也不再默认放行。
/// </summary>
internal static class WebSocketOriginPolicy
{
    public static bool IsAllowed(HttpContext ctx, string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return false;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return false;

        if (!string.Equals(originUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(originUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Origin 只允许 scheme://host[:port]，不允许路径、查询、fragment 或 userinfo。
        if (!string.IsNullOrEmpty(originUri.UserInfo)
            || !string.IsNullOrEmpty(originUri.Query)
            || !string.IsNullOrEmpty(originUri.Fragment)
            || !string.Equals(originUri.AbsolutePath, "/", StringComparison.Ordinal))
        {
            return false;
        }

        var requestHost = ctx.Request.Host;
        if (!string.Equals(originUri.Host, requestHost.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        var requestPort = requestHost.Port ?? (ctx.Request.IsHttps ? 443 : 80);
        if (originUri.Port != requestPort)
            return false;

        var requestScheme = ctx.Request.IsHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        return string.Equals(originUri.Scheme, requestScheme, StringComparison.OrdinalIgnoreCase);
    }
}
