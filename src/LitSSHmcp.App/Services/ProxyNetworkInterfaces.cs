using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LitSSHmcp.App.Services;

public sealed record ProxyBindAddressOption(string Value, string Display);

/// <summary>代理监听地址候选：本机、所有网卡与本机可用局域网 IPv4。</summary>
public static class ProxyNetworkInterfaces
{
    public static IReadOnlyList<ProxyBindAddressOption> GetBindAddressOptions(string? current = null)
    {
        var options = new List<ProxyBindAddressOption>
        {
            new("127.0.0.1", "仅本机 (127.0.0.1)"),
            new("0.0.0.0", "所有网卡 (0.0.0.0)")
        };

        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(address) ||
                        address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        continue;

                    var value = address.ToString();
                    if (options.Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    options.Add(new ProxyBindAddressOption(value,
                        $"{value} · {network.Name}"));
                }
            }
        }
        catch
        {
            // 网络接口枚举失败时只保留本机/所有网卡
        }

        if (!string.IsNullOrWhiteSpace(current) &&
            !options.Any(item => string.Equals(item.Value, current, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new ProxyBindAddressOption(current, current + "（当前配置）"));
        }
        return options;
    }

    public static string? FindLanIpv4()
    {
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(address) ||
                        address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        continue;
                    return address.ToString();
                }
            }
        }
        catch
        {
            // 网络接口枚举失败时回退到本机
        }
        return null;
    }
}
