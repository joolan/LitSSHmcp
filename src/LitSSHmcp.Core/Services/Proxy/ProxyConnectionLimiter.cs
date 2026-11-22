namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>总并发与单客户端 IP 并发限制。租约 Dispose 时归还槽位。</summary>
public sealed class ProxyConnectionLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _perClient = new(StringComparer.Ordinal);
    private int _total;

    public int Total
    {
        get { lock (_gate) return _total; }
    }

    public bool TryAcquire(string? clientKey, int maxTotal, int maxPerClient,
        out IDisposable? lease, out string reason)
    {
        var key = string.IsNullOrWhiteSpace(clientKey) ? "unknown" : clientKey;
        lock (_gate)
        {
            if (_total >= maxTotal)
            {
                lease = null;
                reason = "已达到最大并发连接数。";
                return false;
            }

            _perClient.TryGetValue(key, out var current);
            if (current >= maxPerClient)
            {
                lease = null;
                reason = "该客户端已达到单 IP 并发上限。";
                return false;
            }

            _total++;
            _perClient[key] = current + 1;
            lease = new Lease(this, key);
            reason = string.Empty;
            return true;
        }
    }

    private void Release(string key)
    {
        lock (_gate)
        {
            if (_total > 0)
                _total--;
            if (_perClient.TryGetValue(key, out var current))
            {
                if (current <= 1)
                    _perClient.Remove(key);
                else
                    _perClient[key] = current - 1;
            }
        }
    }

    private sealed class Lease : IDisposable
    {
        private ProxyConnectionLimiter? _owner;
        private readonly string _key;

        public Lease(ProxyConnectionLimiter owner, string key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(_key);
    }
}
