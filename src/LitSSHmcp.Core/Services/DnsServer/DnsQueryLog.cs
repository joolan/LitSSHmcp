namespace LitSSHmcp.Core.Services.DnsServer;

public sealed record DnsLogEntry(
    DateTimeOffset Timestamp,
    string Client,
    string Question,
    string Type,
    string Result,
    string Upstream,
    int DurationMs);

/// <summary>最近查询日志的环形缓冲；默认只保留内存，不落盘。</summary>
public sealed class DnsQueryLog
{
    private readonly object _gate = new();
    private readonly Queue<DnsLogEntry> _items;

    public DnsQueryLog(int capacity = 2000)
    {
        Capacity = Math.Clamp(capacity, 10, 20_000);
        _items = new Queue<DnsLogEntry>(Capacity);
    }

    public int Capacity { get; }

    public void Add(DnsLogEntry entry)
    {
        lock (_gate)
        {
            if (_items.Count >= Capacity)
                _items.Dequeue();
            _items.Enqueue(entry);
        }
    }

    public DnsLogEntry[] Snapshot()
    {
        lock (_gate)
            return _items.ToArray();
    }

    public void Clear()
    {
        lock (_gate)
            _items.Clear();
    }
}
