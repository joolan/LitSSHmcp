using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LitSSHmcp.Core.Services.Proxy;

namespace LitSSHmcp.Core.Services.DnsServer;

/// <summary>客户端来源限制：默认只允许回环与私有网段，避免成为开放解析器。</summary>
public static class DnsClientPolicy
{
    public static bool IsAllowed(IPAddress client, DnsOptions options)
    {
        client = ProxyPolicy.NormalizeAddress(client);
        if (IPAddress.IsLoopback(client))
            return true;

        if (options.AllowedClientCidrs.Count > 0)
        {
            foreach (var value in options.AllowedClientCidrs)
            {
                if (ProxyCidr.TryParse(value, out var cidr) && cidr.Contains(client))
                    return true;
            }
            return false;
        }

        return options.AllowPublicClients || IsPrivateOrLocal(client);
    }

    public static bool IsPrivateOrLocal(IPAddress address)
    {
        address = ProxyPolicy.NormalizeAddress(address);
        if (IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                   bytes[0] == 127;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Loopback) || address.IsIPv6LinkLocal)
                return true;
            return (bytes[0] & 0xFE) == 0xFC; // fc00::/7
        }

        return false;
    }
}

/// <summary>每客户端固定窗口 QPS 限制；0 表示关闭。</summary>
public sealed class DnsClientRateLimiter
{
    private sealed class Window
    {
        public long Start;
        public int Count;
    }

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private int _limit;

    public void Configure(int limit)
    {
        _limit = Math.Clamp(limit, 0, 100000);
        if (_limit == 0)
            _windows.Clear();
    }

    public void Clear() => _windows.Clear();

    public bool TryAcquire(string key)
    {
        if (_limit <= 0)
            return true;

        var now = Environment.TickCount64;
        var window = _windows.GetOrAdd(key, _ => new Window { Start = now });
        lock (window)
        {
            if (now - window.Start >= 1000)
            {
                window.Start = now;
                window.Count = 0;
            }

            if (window.Count >= _limit)
                return false;

            window.Count++;
            return true;
        }
    }

    public void Trim()
    {
        if (_windows.Count < 10_000)
            return;

        var now = Environment.TickCount64;
        foreach (var pair in _windows)
        {
            if (now - pair.Value.Start > 10_000)
                _windows.TryRemove(pair.Key, out _);
        }
    }
}

/// <summary>DNS 域名白名单/黑名单；黑名单优先，白名单为空表示不限制。</summary>
public static class DnsDomainPolicy
{
    public static string? Evaluate(string name, IReadOnlyList<string>? allowed,
        IReadOnlyList<string>? blocked)
    {
        if (blocked is not null)
        {
            foreach (var pattern in blocked)
            {
                if (ProxyDomainMatcher.IsMatch(name, pattern))
                    return "命中域名黑名单";
            }
        }

        if (allowed is null || allowed.Count == 0)
            return null;

        foreach (var pattern in allowed)
        {
            if (ProxyDomainMatcher.IsMatch(name, pattern))
                return null;
        }
        return "不在域名白名单中";
    }
}
