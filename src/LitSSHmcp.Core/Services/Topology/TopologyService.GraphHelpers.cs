using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Topology;

public partial class TopologyService
{
    private static string AssetNodeType(string assetType) => assetType switch
    {
        "ssh" => "ssh",
        "datasource" => "datasource",
        "application" => "application",
        _ => assetType
    };

    private static void AddAccessRouteEdges(
        AppConfig config,
        AssetCanonicalization canonical,
        List<TopologyEdge> edges,
        HashSet<string> keys)
    {
        void Add(string fromRaw, string toRaw, string evidence)
        {
            var from = canonical.ResolveNodeId(fromRaw);
            var to = canonical.ResolveNodeId(toRaw);
            if (from is null || to is null || string.Equals(from, to, StringComparison.Ordinal))
                return;

            var key = EdgeKey(from, to, "accessedVia");
            if (!keys.Add(key))
                return;

            edges.Add(new TopologyEdge
            {
                From = from,
                To = to,
                Type = "accessedVia",
                Evidence = evidence,
                Source = "derived",
                Confidence = 1.0,
                ValidFrom = DateTime.UtcNow,
                LastVerifiedAt = DateTime.UtcNow
            });
        }

        var serverNames = config.Servers.ToDictionary(s => s.Id, s => s.DisplayName, StringComparer.OrdinalIgnoreCase);

        foreach (var server in config.Servers)
        {
            if (string.IsNullOrWhiteSpace(server.ProxyJumpServerId) ||
                !serverNames.TryGetValue(server.ProxyJumpServerId, out var jumpName))
                continue;

            Add(AssetNode.Ssh(server.Id), AssetNode.Ssh(server.ProxyJumpServerId),
                $"config: {server.Name} via jump {jumpName}");
        }

        foreach (var datasource in config.DataSources)
        {
            if (datasource.AccessMode != AccessMode.SshTunnel ||
                string.IsNullOrWhiteSpace(datasource.TunnelServerId) ||
                !serverNames.TryGetValue(datasource.TunnelServerId, out var jumpName))
                continue;

            Add(AssetNode.Ds(datasource.Id), AssetNode.Ssh(datasource.TunnelServerId),
                $"config: {datasource.Name} via tunnel {jumpName}");
        }
    }

    private static Dictionary<string, object?> BuildCanonicalNodeInfo(
        CanonicalAsset asset, CanonicalProfile primary)
    {
        var info = new Dictionary<string, object?>
        {
            ["assetKey"] = asset.AssetKey,
            ["profileIds"] = asset.Profiles.Select(p => p.ProfileId).ToArray(),
            ["profiles"] = asset.Profiles.Select(ToProfileInfo).ToArray(),
            ["aliases"] = asset.Aliases,
            ["identityKinds"] = asset.Identities
                .Select(i => i.IdentityKind)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            ["accessProfileCount"] = asset.Profiles.Count,
            ["host"] = primary.Host,
            ["port"] = primary.Port,
            ["username"] = primary.Username,
            ["description"] = primary.Description,
            ["tags"] = primary.Tags,
            ["group"] = primary.Group
        };

        if (asset.AssetType == "ssh")
        {
            info["disabled"] = primary.Disabled;
            info["internalHost"] = primary.InternalHost;
            info["internalPort"] = primary.InternalPort;
            info["proxyJumpServerId"] = primary.ProxyJumpServerId;
        }
        else if (asset.AssetType == "datasource")
        {
            info["datasourceType"] = primary.DatasourceType;
            info["defaultDatabase"] = primary.DefaultDatabase;
            info["accessMode"] = primary.AccessMode;
            info["tunnelServerId"] = primary.TunnelServerId;
        }
        else if (asset.AssetType == "application")
        {
            info["applicationType"] = primary.ApplicationType;
        }

        return info;
    }

    private static Dictionary<string, object?> ToProfileInfo(CanonicalProfile profile) => new()
    {
        ["id"] = profile.ProfileId,
        ["name"] = profile.Name,
        ["assetKey"] = profile.AssetKey,
        ["host"] = profile.Host,
        ["port"] = profile.Port,
        ["internalHost"] = profile.InternalHost,
        ["proxyJumpServerId"] = profile.ProxyJumpServerId,
        ["accessMode"] = profile.AccessMode,
        ["tunnelServerId"] = profile.TunnelServerId,
        ["description"] = profile.Description
    };
}
