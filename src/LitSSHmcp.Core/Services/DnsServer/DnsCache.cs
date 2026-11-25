namespace LitSSHmcp.Core.Services.DnsServer;

public enum DnsCacheState
{
    None,
    Fresh,
    Stale
}

/// <summary>缓存中的完整响应；TTL 偏移在命中时按已过时间递减。</summary>
public sealed class DnsCacheEntry
{
    public required byte[] Response { get; init; }
    public required int[] TtlOffsets { get; init; }
    public required DateTimeOffset StoredAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required DateTimeOffset StaleUntil { get; init; }
    public required bool Negative { get; init; }
}

/// <summary>进程内 DNS 缓存：容量上限 + 过期清理 + 可选 serve-stale。</summary>
public sealed class DnsCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DnsCacheEntry> _entries = new(StringComparer.Ordinal);
    private int _maxEntries;

    public DnsCache(int maxEntries = 50000)
    {
        _maxEntries = Math.Max(100, maxEntries);
    }

    public int MaxEntries
    {
        get { lock (_gate) return _maxEntries; }
        set
        {
            lock (_gate)
            {
                _maxEntries = Math.Max(100, value);
                TrimLocked();
            }
        }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public bool TryGetFresh(string key, DateTimeOffset now, out DnsCacheEntry entry)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
                return false;

            if (entry.ExpiresAt > now)
                return true;

            if (entry.StaleUntil <= now)
                _entries.Remove(key);
            return false;
        }
    }

    public bool TryGetStale(string key, DateTimeOffset now, out DnsCacheEntry entry)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
                return false;

            if (entry.ExpiresAt <= now && entry.StaleUntil > now)
                return true;

            if (entry.StaleUntil <= now)
                _entries.Remove(key);
            return false;
        }
    }

    public void Set(string key, DnsCacheEntry entry)
    {
        lock (_gate)
        {
            _entries[key] = entry;
            TrimLocked();
        }
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }

    private void TrimLocked()
    {
        if (_entries.Count <= _maxEntries)
            return;

        // 按过期时间批量淘汰，避免每次插入都做一次全量排序。
        var removeCount = Math.Max(1, _maxEntries / 10);
        foreach (var key in _entries
                     .OrderBy(pair => pair.Value.ExpiresAt)
                     .Take(removeCount)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.Remove(key);
        }
    }
}
