using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Topology;

public sealed class CanonicalProfile
{
    public string AssetType { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? AssetKey { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? InternalHost { get; init; }
    public int? InternalPort { get; init; }
    public string? ProxyJumpServerId { get; init; }
    public string? AccessMode { get; init; }
    public string? TunnelServerId { get; init; }
    public string? Username { get; init; }
    public string? DatasourceType { get; init; }
    public string? DefaultDatabase { get; init; }
    public string? ApplicationType { get; init; }
    public string? Group { get; init; }
    public string[] Tags { get; init; } = Array.Empty<string>();
    public bool Disabled { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public string[] Aliases { get; init; } = Array.Empty<string>();
}

public sealed class CanonicalAsset
{
    public string NodeId { get; init; } = string.Empty;
    public string AssetType { get; init; } = string.Empty;
    public string CanonicalProfileId { get; init; } = string.Empty;
    public string? AssetKey { get; init; }
    public List<CanonicalProfile> Profiles { get; init; } = new();
    public List<AssetIdentityRecord> Identities { get; init; } = new();
    public string[] Aliases { get; init; } = Array.Empty<string>();
}

public sealed class AssetCanonicalization
{
    private readonly Dictionary<string, string> _profileToNodeId;

    internal AssetCanonicalization(
        Dictionary<string, CanonicalAsset> assets,
        Dictionary<string, string> profileToNodeId)
    {
        Assets = assets;
        _profileToNodeId = profileToNodeId;
    }

    public IReadOnlyDictionary<string, CanonicalAsset> Assets { get; }

    public string? ResolveNodeId(string? nodeOrProfileId)
    {
        if (string.IsNullOrWhiteSpace(nodeOrProfileId))
            return null;

        var id = nodeOrProfileId.Trim();
        if (Assets.ContainsKey(id))
            return id;

        if (_profileToNodeId.TryGetValue(id, out var nodeId))
            return nodeId;

        foreach (var prefix in new[] { AssetNode.SshPrefix, AssetNode.DsPrefix, AssetNode.AppPrefix })
        {
            if (_profileToNodeId.TryGetValue(prefix + id, out nodeId))
                return nodeId;
        }

        return null;
    }

    public CanonicalAsset? FindByProfileId(string? profileId)
    {
        var nodeId = ResolveNodeId(profileId);
        return nodeId != null && Assets.TryGetValue(nodeId, out var asset) ? asset : null;
    }

    public string[] ProfileIdsOf(string nodeId) =>
        Assets.TryGetValue(nodeId, out var asset)
            ? asset.Profiles.Select(p => p.ProfileId).ToArray()
            : Array.Empty<string>();
}

public static class AssetCanonicalizer
{
    public static AssetCanonicalization Build(
        AppConfig config,
        IReadOnlyList<AssetIdentityRecord>? identities = null)
    {
        identities ??= Array.Empty<AssetIdentityRecord>();
        var profiles = new List<CanonicalProfile>();

        profiles.AddRange(config.Servers.Select(server => new CanonicalProfile
        {
            AssetType = "ssh",
            ProfileId = server.Id,
            Name = server.Name,
            AssetKey = NormalizeKey(server.AssetKey),
            Host = server.Host,
            Port = server.Port,
            InternalHost = server.InternalHost,
            InternalPort = server.InternalPort,
            ProxyJumpServerId = server.ProxyJumpServerId,
            Username = server.Username,
            Group = server.Group,
            Tags = server.Tags ?? Array.Empty<string>(),
            Disabled = server.Disabled,
            Description = server.Description,
            CreatedAt = server.CreatedAt,
            Aliases = BuildSshAliases(server)
        }));

        profiles.AddRange(config.DataSources.Select(datasource => new CanonicalProfile
        {
            AssetType = "datasource",
            ProfileId = datasource.Id,
            Name = datasource.Name,
            AssetKey = NormalizeKey(datasource.AssetKey),
            Host = datasource.Host,
            Port = datasource.Port,
            AccessMode = datasource.AccessMode.ToString(),
            TunnelServerId = datasource.TunnelServerId,
            Username = datasource.Username,
            DatasourceType = datasource.Type,
            DefaultDatabase = datasource.DefaultDatabase,
            Group = datasource.Group,
            Tags = datasource.Tags ?? Array.Empty<string>(),
            Description = datasource.Description,
            CreatedAt = datasource.CreatedAt,
            Aliases = BuildDatasourceAliases(datasource)
        }));

        profiles.AddRange(config.Applications.Select(application => new CanonicalProfile
        {
            AssetType = "application",
            ProfileId = application.Id,
            Name = application.Name,
            AssetKey = NormalizeKey(application.AssetKey),
            Host = application.Host,
            Port = application.Port,
            ApplicationType = application.Type,
            Group = application.Group,
            Tags = application.Tags ?? Array.Empty<string>(),
            Description = application.Description,
            CreatedAt = DateTime.MinValue,
            Aliases = BuildApplicationAliases(application)
        }));

        if (profiles.Count == 0)
            return new AssetCanonicalization(new(), new(StringComparer.Ordinal));

        var profileIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < profiles.Count; i++)
            profileIndex[NodePrefix(profiles[i].AssetType) + profiles[i].ProfileId] = i;

        var parent = Enumerable.Range(0, profiles.Count).ToArray();

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

        bool CanUnion(int left, int right)
        {
            var a = profiles[left];
            var b = profiles[right];
            if (a.AssetType != b.AssetType)
                return false;

            var leftKey = NormalizeKey(a.AssetKey);
            var rightKey = NormalizeKey(b.AssetKey);
            return leftKey is null || rightKey is null ||
                   string.Equals(leftKey, rightKey, StringComparison.OrdinalIgnoreCase);
        }

        void TryUnion(int left, int right)
        {
            if (CanUnion(left, right))
                Union(left, right);
        }

        var assetKeyOwners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            if (string.IsNullOrWhiteSpace(profile.AssetKey))
                continue;

            var key = $"{profile.AssetType}|{profile.AssetKey}";
            if (assetKeyOwners.TryGetValue(key, out var owner))
                TryUnion(owner, i);
            else
                assetKeyOwners[key] = i;
        }

        var identityOwners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            foreach (var identityKey in IdentityKeys(profile, profile.AssetType, identities))
            {
                var key = $"{profile.AssetType}|{identityKey}";
                if (identityOwners.TryGetValue(key, out var owner))
                    TryUnion(owner, i);
                else
                    identityOwners[key] = i;
            }
        }

        foreach (var group in DuplicateAssetDetector.FindAll(config))
        {
            var indices = group.Items
                .Select(item => profileIndex.TryGetValue(NodePrefix(item.AssetType) + item.Id, out var index)
                    ? index
                    : -1)
                .Where(index => index >= 0)
                .ToArray();

            for (var i = 0; i < indices.Length; i++)
            {
                for (var j = i + 1; j < indices.Length; j++)
                    TryUnion(indices[i], indices[j]);
            }
        }

        var assets = new Dictionary<string, CanonicalAsset>(StringComparer.Ordinal);
        var profileToNodeId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var component in Enumerable.Range(0, profiles.Count).GroupBy(Find))
        {
            var members = component.Select(index => profiles[index])
                .OrderBy(p => p.CreatedAt)
                .ThenBy(p => p.ProfileId, StringComparer.Ordinal)
                .ToList();

            var keyed = members
                .Where(p => !string.IsNullOrWhiteSpace(p.AssetKey))
                .OrderBy(p => p.CreatedAt)
                .ThenBy(p => p.ProfileId, StringComparer.Ordinal)
                .FirstOrDefault();

            var type = members[0].AssetType;
            var canonicalProfile = keyed ?? members[0];
            var nodeId = NodePrefix(type) + TopologyIds.SanitizeId(
                canonicalProfile.AssetKey ?? canonicalProfile.ProfileId);

            var identitiesForAsset = members
                .SelectMany(member => IdentityRecords(member, type, identities))
                .GroupBy(r => $"{r.IdentityKind}|{r.IdentityValue}", StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            var aliases = members
                .SelectMany(m => m.Aliases)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var asset = new CanonicalAsset
            {
                NodeId = nodeId,
                AssetType = type,
                CanonicalProfileId = canonicalProfile.ProfileId,
                AssetKey = canonicalProfile.AssetKey,
                Profiles = members,
                Identities = identitiesForAsset,
                Aliases = aliases
            };

            assets[nodeId] = asset;
            foreach (var member in members)
                profileToNodeId[NodePrefix(type) + member.ProfileId] = nodeId;
        }

        return new AssetCanonicalization(assets, profileToNodeId);
    }

    private static string NodePrefix(string assetType) => assetType switch
    {
        "ssh" => AssetNode.SshPrefix,
        "datasource" => AssetNode.DsPrefix,
        "application" => AssetNode.AppPrefix,
        _ => string.Empty
    };

    private static string[] IdentityKeys(
        CanonicalProfile profile,
        string assetType,
        IReadOnlyList<AssetIdentityRecord> identities) =>
        IdentityRecords(profile, assetType, identities)
            .Select(r => $"{r.IdentityKind}|{r.IdentityValue}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<AssetIdentityRecord> IdentityRecords(
        CanonicalProfile profile,
        string assetType,
        IReadOnlyList<AssetIdentityRecord> identities)
    {
        var assetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { profile.ProfileId };
        if (!string.IsNullOrWhiteSpace(profile.AssetKey))
            assetIds.Add(profile.AssetKey);

        return identities.Where(r =>
            string.Equals(r.AssetType, assetType, StringComparison.OrdinalIgnoreCase) &&
            assetIds.Contains(r.AssetId));
    }

    private static string? NormalizeKey(string? assetKey) =>
        string.IsNullOrWhiteSpace(assetKey) ? null : assetKey.Trim();

    private static string[] BuildSshAliases(SshServerConfig server)
    {
        var values = new List<string>();
        AddIf(values, server.Host);
        AddIf(values, server.InternalHost);
        AddIf(values, server.Aliases);
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildDatasourceAliases(DataSourceConfig datasource)
    {
        var values = new List<string>();
        AddIf(values, datasource.Host);
        AddIf(values, datasource.Aliases);
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildApplicationAliases(ApplicationConfig application)
    {
        var values = new List<string>();
        AddIf(values, application.Host);
        AddIf(values, application.Aliases);
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddIf(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(value);
    }

    private static void AddIf(List<string> values, IEnumerable<string>? items)
    {
        if (items is null)
            return;
        foreach (var item in items)
            AddIf(values, item);
    }
}
