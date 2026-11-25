using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LitSSHmcp.Core.Services.Proxy;

namespace LitSSHmcp.Core.Services.DnsServer;

/// <summary>
/// 响应速率限制（RRL）：按客户端网段 + 域名 + 类型做令牌桶，仅用于 UDP。
/// 超限时返回 TC=1，让客户端改走 TCP，避免 UDP 被用于放大攻击。
/// </summary>
public sealed class DnsResponseRateLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public long LastMs;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets =
        new(StringComparer.Ordinal);

    private int _rate;
    private int _burst;
    private bool _enabled;

    public bool Enabled => _enabled;

    public void Configure(bool enabled, int responsesPerSecond, int burst)
    {
        _enabled = enabled;
        _rate = Math.Clamp(responsesPerSecond, 1, 100_000);
        _burst = Math.Clamp(burst, 1, 200_000);
        _buckets.Clear();
    }

    public bool TryAcquire(string key)
    {
        if (!_enabled)
            return true;

        var now = Environment.TickCount64;
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = _burst, LastMs = now });
        lock (bucket)
        {
            var elapsed = now - bucket.LastMs;
            if (elapsed > 0)
            {
                bucket.Tokens = Math.Min(_burst, bucket.Tokens + elapsed * _rate / 1000.0);
                bucket.LastMs = now;
            }

            if (bucket.Tokens < 1)
                return false;

            bucket.Tokens -= 1;
            return true;
        }
    }

    /// <summary>清理长时间未使用的桶，避免客户端网段/域名组合无限增长。</summary>
    public void Trim()
    {
        if (_buckets.Count < 10_000)
            return;

        var now = Environment.TickCount64;
        foreach (var pair in _buckets)
        {
            lock (pair.Value)
            {
                if (now - pair.Value.LastMs > 60_000)
                    _buckets.TryRemove(pair.Key, out _);
            }
        }
    }

    public void Clear() => _buckets.Clear();

    /// <summary>RRL 聚合粒度：IPv4 按 /24，IPv6 按 /56。</summary>
    public static string SubnetKey(IPAddress address)
    {
        address = ProxyPolicy.NormalizeAddress(address);
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";

        var prefix = new byte[16];
        Array.Copy(bytes, prefix, 7);
        return new IPAddress(prefix) + "/56";
    }
}
