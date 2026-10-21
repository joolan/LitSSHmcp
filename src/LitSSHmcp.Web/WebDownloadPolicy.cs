namespace LitSSHmcp.Web;

/// <summary>Web 会话文件下载边界（P2-8）：单文件上限、每用户并发与单次响应超时。</summary>
public sealed class WebDownloadPolicy
{
    /// <summary>单文件下载上限；超过返回 413，避免一次拉取超大文件。</summary>
    public long MaxBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>每用户同时下载数上限；&lt;=0 时 fail-closed（全部拒绝）。</summary>
    public int MaxConcurrentPerUser { get; init; } = 4;

    /// <summary>单次响应墙钟超时；&lt;=0 表示不额外限制（仍受请求取消约束）。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// 进程内下载并发限制：按用户计数，下载结束 Dispose 释放。计数/释放同锁，
/// 避免并发请求把上限撑破；未提供 key 或 limit&lt;=0 时拒绝（fail-closed）。
/// </summary>
public sealed class WebDownloadLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

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

    private sealed class Lease(WebDownloadLimiter owner, string key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(key);
        }
    }
}
