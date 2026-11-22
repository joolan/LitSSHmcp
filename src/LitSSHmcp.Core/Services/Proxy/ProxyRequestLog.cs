namespace LitSSHmcp.Core.Services.Proxy;

public sealed record ProxyLogEntry(
    DateTimeOffset Timestamp,
    string Protocol,
    string Client,
    string Username,
    string Target,
    string Result,
    long BytesIn,
    long BytesOut);

/// <summary>最近请求日志的环形缓冲；只保留 UI 展示所需字段，不记录凭据。</summary>
public sealed class ProxyRequestLog
{
    private readonly object _gate = new();
    private readonly Queue<ProxyLogEntry> _items;

    public ProxyRequestLog(int capacity = 500)
    {
        Capacity = Math.Clamp(capacity, 10, 10_000);
        _items = new Queue<ProxyLogEntry>(Capacity);
    }

    public int Capacity { get; }

    public void Add(ProxyLogEntry entry)
    {
        lock (_gate)
        {
            if (_items.Count >= Capacity)
                _items.Dequeue();
            _items.Enqueue(entry);
        }
    }

    public ProxyLogEntry[] Snapshot()
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
