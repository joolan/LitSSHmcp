using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Topology;

/// <summary>
/// 拓扑节点 ID 的统一生成与主机名规范化。
/// 待确认节点必须带服务器维度，避免两台机器上的同名服务被合并成同一个节点。
/// </summary>
public static class TopologyIds
{
    public static string PendingApp(string serverId, string name) =>
        $"app:disc:{SanitizeId(serverId)}:{SanitizeId(name)}";

    public static string PendingLocalDatasource(string serverId, string type, int? port = null) =>
        $"ds:disc:local:{SanitizeId(serverId)}:{SanitizeId(type)}" +
        (port is > 0 ? $":{port.Value}" : string.Empty);

    public static string PendingRemoteDatasource(string host, int port) =>
        $"ds:disc:remote:{SanitizeId(NormalizeHost(host))}-{port}";

    public static string PendingSsh(string host) =>
        $"ssh:disc:{SanitizeId(NormalizeHost(host))}";

    public static string PendingMq(string host, int port) =>
        $"mq:disc:{SanitizeId(NormalizeHost(host))}-{port}";

    public static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return string.Empty;

        var value = host.Trim().Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(value, out var ip))
            return ip.ToString().ToLowerInvariant();

        return value.ToLowerInvariant();
    }

    public static string NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return string.Empty;

        var value = domain.Trim().TrimEnd('.').ToLowerInvariant();
        var wildcard = value.StartsWith("*.", StringComparison.Ordinal) ? "*." : string.Empty;
        var core = wildcard.Length > 0 ? value[2..] : value;

        try
        {
            core = new IdnMapping().GetAscii(core).ToLowerInvariant();
        }
        catch
        {
            // 非法 IDN 保持原值，避免发现流程因域名格式异常中断
        }

        return wildcard + core;
    }

    public static string SanitizeId(string? value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9\-_.]", "-");
        return normalized.Length == 0 ? "unknown" : normalized;
    }
}

/// <summary>
/// 主机匹配的统一实现：支持 Host、InternalHost、服务器名称、手工别名与 DNS。
/// </summary>
public static class TopologyHost
{
    public static bool Matches(
        string? configuredHost,
        string? actualHost,
        Dictionary<string, string[]>? dnsCache = null)
    {
        if (string.IsNullOrWhiteSpace(configuredHost) || string.IsNullOrWhiteSpace(actualHost))
            return false;

        var configured = TopologyIds.NormalizeHost(configuredHost);
        var actual = TopologyIds.NormalizeHost(actualHost);
        if (configured.Length == 0 || actual.Length == 0)
            return false;

        if (string.Equals(configured, actual, StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(configured, out var configuredIp) &&
            IPAddress.TryParse(actual, out var actualIp))
            return configuredIp.Equals(actualIp);

        try
        {
            string[] addresses;
            if (dnsCache != null)
            {
                if (!dnsCache.TryGetValue(configured, out addresses!))
                {
                    addresses = Dns.GetHostAddresses(configured)
                        .Select(a => a.ToString())
                        .ToArray();
                    dnsCache[configured] = addresses;
                }
            }
            else
            {
                addresses = Dns.GetHostAddresses(configured)
                    .Select(a => a.ToString())
                    .ToArray();
            }

            return addresses.Any(a => TopologyIds.NormalizeHost(a) == actual);
        }
        catch
        {
            return false;
        }
    }

    public static bool MatchesServer(
        SshServerConfig server,
        string? host,
        Dictionary<string, string[]>? dnsCache = null)
    {
        if (!string.IsNullOrWhiteSpace(server.Name) &&
            string.Equals(server.Name.Trim(), host?.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var alias in ServerAliases(server))
        {
            if (Matches(alias, host, dnsCache) || Matches(host, alias, dnsCache))
                return true;
        }

        return false;
    }

    public static IEnumerable<string> ServerAliases(SshServerConfig server)
    {
        yield return server.Host;

        if (!string.IsNullOrWhiteSpace(server.InternalHost))
            yield return server.InternalHost;

        foreach (var alias in server.Aliases ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias;
        }
    }

    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var h = host.Trim().Trim('[', ']').ToLowerInvariant();
        return h is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0" or "localhost.localdomain"
            || h.StartsWith("127.", StringComparison.Ordinal);
    }
}
