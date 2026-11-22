using System.Net;
using System.Net.Sockets;

namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>客户端白名单中的一条 IP / CIDR。</summary>
public readonly record struct ProxyCidr(IPAddress Network, int PrefixLength)
{
    public bool Contains(IPAddress address)
    {
        address = ProxyPolicy.NormalizeAddress(address);
        var network = ProxyPolicy.NormalizeAddress(Network);
        if (network.AddressFamily != address.AddressFamily)
            return false;

        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (networkBytes.Length != addressBytes.Length)
            return false;

        var fullBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != addressBytes[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        if (fullBytes >= networkBytes.Length)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }

    public static bool TryParse(string? text, out ProxyCidr cidr)
    {
        cidr = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        var slash = value.IndexOf('/');
        var addressText = slash >= 0 ? value[..slash] : value;
        if (!IPAddress.TryParse(addressText, out var address))
            return false;

        var originalWasMapped = address.IsIPv4MappedToIPv6;
        address = ProxyPolicy.NormalizeAddress(address);
        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxPrefix;
        if (slash >= 0)
        {
            if (!int.TryParse(value[(slash + 1)..], out prefix))
                return false;
            if (originalWasMapped && prefix >= 96)
                prefix -= 96;
            if (prefix < 0 || prefix > maxPrefix)
                return false;
        }

        cidr = new ProxyCidr(address, prefix);
        return true;
    }
}

public sealed record ProxyTargetDecision(bool Allowed, string Reason, IPAddress[] Addresses)
{
    public static ProxyTargetDecision Reject(string reason) => new(false, reason, Array.Empty<IPAddress>());
}

/// <summary>客户端来源限制、目标端口与私有/保留地址策略。</summary>
public static class ProxyPolicy
{
    public static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>白名单为空表示不限制；配置了白名单但没有一条合法时 fail-closed。</summary>
    public static bool IsClientAllowed(IPAddress? client, IReadOnlyList<string>? cidrs)
    {
        if (cidrs is null || cidrs.Count == 0)
            return true;
        if (client is null)
            return false;

        foreach (var value in cidrs)
        {
            if (!ProxyCidr.TryParse(value, out var cidr))
                continue;
            if (cidr.Contains(client))
                return true;
        }
        return false;
    }

    /// <summary>校验目标端口、解析域名，并在连接前对解析结果做私有/保留地址检查。</summary>
    public static async Task<ProxyTargetDecision> EvaluateAsync(
        string host, int port, ProxyOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return ProxyTargetDecision.Reject("目标地址为空。");

        var domainError = ProxyDomainMatcher.Evaluate(host, options.AllowedDomains,
            options.BlockedDomains);
        if (domainError is not null)
            return ProxyTargetDecision.Reject(domainError);

        if (port is < 1 or > 65535)
            return ProxyTargetDecision.Reject($"目标端口 {port} 不合法。");

        if (!options.AllowAllPorts && !options.AllowedPorts.Contains(port))
            return ProxyTargetDecision.Reject($"目标端口 {port} 不在允许列表中。");

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { NormalizeAddress(literal) };
        }
        else
        {
            try
            {
                addresses = (await Dns.GetHostAddressesAsync(host, ct))
                    .Select(NormalizeAddress)
                    .Distinct()
                    .ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ProxyTargetDecision.Reject("域名解析失败：" + ex.Message);
            }
        }

        if (addresses.Length == 0)
            return ProxyTargetDecision.Reject($"域名 {host} 没有可用地址。");

        if (options.BlockPrivateDestinations)
        {
            foreach (var address in addresses)
            {
                if (IsPrivateOrReserved(address))
                    return ProxyTargetDecision.Reject(
                        $"目标 {host} 解析到内网/保留地址 {address}，已被策略阻止。");
            }
        }

        return new ProxyTargetDecision(true, string.Empty, addresses);
    }

    /// <summary>回环、私有、链路本地、组播、文档/保留地址。</summary>
    public static bool IsPrivateOrReserved(IPAddress address)
    {
        address = NormalizeAddress(address);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) ||
                address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
                return true;

            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC)                 // fc00::/7
                return true;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
                return true;                           // 2001:db8::/32
            if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 &&
                b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
                b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
                return true;                           // ::/96（IPv4-compatible）
            return false;
        }

        var v4 = address.GetAddressBytes();
        if (v4.Length != 4)
            return true;

        return v4[0] switch
        {
            0 => true,                                                   // 0.0.0.0/8
            10 => true,                                                  // 10.0.0.0/8
            100 when v4[1] is >= 64 and <= 127 => true,                  // 100.64.0.0/10 CGNAT
            127 => true,                                                 // 127.0.0.0/8
            169 when v4[1] == 254 => true,                               // 169.254.0.0/16
            172 when v4[1] is >= 16 and <= 31 => true,                   // 172.16.0.0/12
            192 when v4[1] == 0 && v4[2] is 0 or 2 => true,              // 192.0.0.0/24, 192.0.2.0/24
            192 when v4[1] == 168 => true,                               // 192.168.0.0/16
            198 when v4[1] is 18 or 19 => true,                          // 198.18.0.0/15
            198 when v4[1] == 51 && v4[2] == 100 => true,                // 198.51.100.0/24
            203 when v4[1] == 0 && v4[2] == 113 => true,                 // 203.0.113.0/24
            >= 224 => true,                                              // 组播/保留/广播
            _ => false
        };
    }
}
