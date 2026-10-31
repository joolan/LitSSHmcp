using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Topology;

/// <summary>
/// 检测“同一接入端点”的多个配置：只做提示，不阻止保存。
/// 不同账号、不同跳板/隧道属于合法的多个接入配置；拓扑归并由后续身份层完成。
/// </summary>
public static class DuplicateAssetDetector
{
    public static List<DuplicateAssetGroup> FindAll(AppConfig config)
    {
        var dnsCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<DuplicateAssetGroup>();
        groups.AddRange(FindSshGroups(config, dnsCache));
        groups.AddRange(FindDatasourceGroups(config, dnsCache));
        return groups;
    }

    public static List<DuplicateAssetItem> FindSshMatches(
        AppConfig config, SshServerConfig candidate, string? excludeId = null)
    {
        var dnsCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        excludeId ??= candidate.Id;

        return config.Servers
            .Where(s => !string.Equals(s.Id, excludeId, StringComparison.OrdinalIgnoreCase))
            .Where(s => SshProfilesMatch(candidate, s, dnsCache))
            .Select(ToSshItem)
            .ToList();
    }

    public static List<DuplicateAssetItem> FindDatasourceMatches(
        AppConfig config, DataSourceConfig candidate, string? excludeId = null)
    {
        var dnsCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        excludeId ??= candidate.Id;

        return config.DataSources
            .Where(d => !string.Equals(d.Id, excludeId, StringComparison.OrdinalIgnoreCase))
            .Where(d => DatasourceProfilesMatch(candidate, d, dnsCache))
            .Select(ToDatasourceItem)
            .ToList();
    }

    private static List<DuplicateAssetGroup> FindSshGroups(
        AppConfig config, Dictionary<string, string[]> dnsCache)
    {
        return BuildGroups(
            config.Servers,
            (a, b) => SshProfilesMatch(a, b, dnsCache),
            ToSshItem,
            "ssh",
            "Host/Port 重叠，可能是同一服务器的多个接入配置");
    }

    private static List<DuplicateAssetGroup> FindDatasourceGroups(
        AppConfig config, Dictionary<string, string[]> dnsCache)
    {
        return BuildGroups(
            config.DataSources,
            (a, b) => DatasourceProfilesMatch(a, b, dnsCache),
            ToDatasourceItem,
            "datasource",
            "类型/端口/主机重叠，可能是同一数据库实例的多个连接配置");
    }

    private static List<DuplicateAssetGroup> BuildGroups<T>(
        IReadOnlyList<T> items,
        Func<T, T, bool> match,
        Func<T, DuplicateAssetItem> toItem,
        string assetType,
        string reason)
    {
        if (items.Count < 2)
            return new List<DuplicateAssetGroup>();

        var parent = Enumerable.Range(0, items.Count).ToArray();

        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        void Union(int left, int right)
        {
            var a = Find(left);
            var b = Find(right);
            if (a != b)
                parent[b] = a;
        }

        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                if (match(items[i], items[j]))
                    Union(i, j);
            }
        }

        return Enumerable.Range(0, items.Count)
            .GroupBy(Find)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var members = g.Select(index => toItem(items[index]))
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Id, StringComparer.Ordinal)
                    .ToArray();

                return new DuplicateAssetGroup
                {
                    AssetType = assetType,
                    MatchKey = string.Join(" / ", members
                        .Select(x => $"{TopologyIds.NormalizeHost(x.Host)}:{x.Port}")
                        .Distinct(StringComparer.OrdinalIgnoreCase)),
                    Reason = reason,
                    Items = members
                };
            })
            .OrderBy(g => g.AssetType, StringComparer.Ordinal)
            .ThenBy(g => g.MatchKey, StringComparer.Ordinal)
            .ToList();
    }

    private static bool SshProfilesMatch(
        SshServerConfig left, SshServerConfig right, Dictionary<string, string[]> dnsCache)
    {
        if (!PortsOverlap(left, right))
            return false;

        if (TopologyHost.IsLocalHost(left.Host) && TopologyHost.IsLocalHost(right.Host) &&
            !string.Equals(left.ProxyJumpServerId, right.ProxyJumpServerId, StringComparison.OrdinalIgnoreCase))
            return false;

        return HostsOverlap(
            SshHosts(left), SshHosts(right), dnsCache);
    }

    private static bool DatasourceProfilesMatch(
        DataSourceConfig left, DataSourceConfig right, Dictionary<string, string[]> dnsCache)
    {
        if (!string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase))
            return false;

        if (left.Port != right.Port)
            return false;

        if (TopologyHost.IsLocalHost(left.Host) && TopologyHost.IsLocalHost(right.Host))
        {
            if (left.AccessMode != right.AccessMode)
                return false;

            if (left.AccessMode == AccessMode.SshTunnel &&
                !string.Equals(left.TunnelServerId, right.TunnelServerId, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return HostsOverlap(
            DatasourceHosts(left), DatasourceHosts(right), dnsCache);
    }

    private static bool PortsOverlap(SshServerConfig left, SshServerConfig right)
    {
        var leftPorts = new[] { left.Port, left.InternalPort ?? 0 }.Where(x => x > 0).ToHashSet();
        return new[] { right.Port, right.InternalPort ?? 0 }
            .Where(x => x > 0)
            .Any(leftPorts.Contains);
    }

    private static bool HostsOverlap(
        IEnumerable<string> left,
        IEnumerable<string> right,
        Dictionary<string, string[]> dnsCache)
    {
        var leftHosts = left.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var rightHosts = right.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

        return leftHosts.Any(a => rightHosts.Any(b =>
            TopologyHost.Matches(a, b, dnsCache) ||
            TopologyHost.Matches(b, a, dnsCache)));
    }

    private static IEnumerable<string> SshHosts(SshServerConfig server)
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

    private static IEnumerable<string> DatasourceHosts(DataSourceConfig datasource)
    {
        yield return datasource.Host;

        foreach (var alias in datasource.Aliases ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias;
        }
    }

    private static DuplicateAssetItem ToSshItem(SshServerConfig server) => new()
    {
        AssetType = "ssh",
        Id = server.Id,
        Name = server.Name,
        Host = server.Host,
        Port = server.Port,
        Detail = BuildSshDetail(server)
    };

    private static DuplicateAssetItem ToDatasourceItem(DataSourceConfig datasource) => new()
    {
        AssetType = "datasource",
        Id = datasource.Id,
        Name = datasource.Name,
        Host = datasource.Host,
        Port = datasource.Port,
        Detail = BuildDatasourceDetail(datasource)
    };

    private static string BuildSshDetail(SshServerConfig server)
    {
        var parts = new List<string> { $"user={server.Username}" };
        if (!string.IsNullOrWhiteSpace(server.InternalHost))
            parts.Add($"internal={server.InternalHost}:{server.InternalPort ?? server.Port}");
        if (!string.IsNullOrWhiteSpace(server.ProxyJumpServerId))
            parts.Add($"jump={server.ProxyJumpServerId}");
        if (server.Disabled)
            parts.Add("disabled");
        if (server.Aliases is { Length: > 0 })
            parts.Add($"aliases={string.Join(",", server.Aliases)}");
        return string.Join(" · ", parts);
    }

    private static string BuildDatasourceDetail(DataSourceConfig datasource)
    {
        var parts = new List<string>
        {
            $"type={datasource.Type}",
            $"mode={datasource.AccessMode}"
        };
        if (!string.IsNullOrWhiteSpace(datasource.TunnelServerId))
            parts.Add($"tunnel={datasource.TunnelServerId}");
        if (!string.IsNullOrWhiteSpace(datasource.DefaultDatabase))
            parts.Add($"db={datasource.DefaultDatabase}");
        if (datasource.Aliases is { Length: > 0 })
            parts.Add($"aliases={string.Join(",", datasource.Aliases)}");
        return string.Join(" · ", parts);
    }
}
