namespace LitSSHmcp.Core.Services.Proxy;

public sealed record ProxyAuthFailureResult(
    int Count,
    bool Blocked,
    TimeSpan RetryAfter,
    bool ShouldAudit);

/// <summary>
/// 按客户端 IP 统计认证失败：滑动窗口内超过上限后临时封禁。
/// 只保存时间戳，不保存密码；成功认证会清除该客户端的失败记录。
/// </summary>
public sealed class ProxyAuthFailureTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool IsBlocked(string clientKey, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        lock (_gate)
        {
            if (!_entries.TryGetValue(clientKey, out var entry) ||
                entry.BlockedUntil is not { } blockedUntil)
                return false;

            var now = DateTimeOffset.UtcNow;
            if (blockedUntil <= now)
            {
                entry.BlockedUntil = null;
                entry.Failures.Clear();
                return false;
            }

            retryAfter = blockedUntil - now;
            return true;
        }
    }

    public ProxyAuthFailureResult RecordFailure(string clientKey, int limit,
        int windowSeconds, int blockSeconds)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            Prune(now, windowSeconds);

            if (!_entries.TryGetValue(clientKey, out var entry))
            {
                entry = new Entry();
                _entries[clientKey] = entry;
            }

            entry.LastSeen = now;
            if (entry.BlockedUntil is { } blockedUntil && blockedUntil > now)
            {
                var remain = blockedUntil - now;
                return new ProxyAuthFailureResult(entry.Failures.Count, true, remain, false);
            }

            entry.BlockedUntil = null;
            entry.Failures.Enqueue(now);
            var cutoff = now.AddSeconds(-Math.Max(30, windowSeconds));
            while (entry.Failures.Count > 0 && entry.Failures.Peek() < cutoff)
                entry.Failures.Dequeue();

            var count = entry.Failures.Count;
            var blocked = limit > 0 && count >= limit;
            if (blocked)
            {
                entry.BlockedUntil = now.AddSeconds(Math.Max(30, blockSeconds));
                entry.Failures.Clear();
            }

            var shouldAudit = count == 1 || (limit > 0 && count >= limit) || count % 5 == 0;
            var retry = entry.BlockedUntil is { } until ? until - now : TimeSpan.Zero;
            return new ProxyAuthFailureResult(count, blocked, retry, shouldAudit);
        }
    }

    public void Reset(string clientKey)
    {
        lock (_gate)
            _entries.Remove(clientKey);
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }

    private void Prune(DateTimeOffset now, int windowSeconds)
    {
        if (_entries.Count < 512)
            return;

        var cutoff = now.AddSeconds(-Math.Max(60, windowSeconds * 2));
        var stale = _entries
            .Where(pair => pair.Value.BlockedUntil is null && pair.Value.LastSeen < cutoff)
            .Select(pair => pair.Key)
            .Take(Math.Max(64, _entries.Count / 4))
            .ToArray();
        foreach (var key in stale)
            _entries.Remove(key);

        if (_entries.Count >= 4096)
            _entries.Clear();
    }

    private sealed class Entry
    {
        public Queue<DateTimeOffset> Failures { get; } = new();
        public DateTimeOffset? BlockedUntil { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }
}
