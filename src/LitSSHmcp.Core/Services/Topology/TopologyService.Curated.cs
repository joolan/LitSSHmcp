using System.Text.Json;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Topology;

public partial class TopologyService
{
    public async Task<TopologyGraph> GetCuratedGraphAsync(CancellationToken ct = default)
    {
        var config = await _configService.LoadConfigAsync();
        IReadOnlyList<AssetIdentityRecord> identities = Array.Empty<AssetIdentityRecord>();
        if (_identityStore != null)
        {
            try
            {
                await _identityStore.InitializeAsync();
                identities = await _identityStore.GetAllAsync();
            }
            catch
            {
                // 身份库不可用时退化为按配置展示
            }
        }

        var canonical = AssetCanonicalizer.Build(config, identities);
        var nodes = new Dictionary<string, TopologyNode>(StringComparer.Ordinal);
        var edges = new List<TopologyEdge>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var asset in canonical.Assets.Values)
        {
            var primary = asset.Profiles.FirstOrDefault(p => p.ProfileId == asset.CanonicalProfileId)
                          ?? asset.Profiles.First();
            nodes[asset.NodeId] = new TopologyNode
            {
                Id = asset.NodeId,
                Label = string.IsNullOrWhiteSpace(primary.Name)
                    ? asset.AssetKey ?? asset.NodeId
                    : primary.Name,
                Type = AssetNodeType(asset.AssetType),
                Source = "manual",
                Info = BuildCanonicalNodeInfo(asset, primary)
            };
        }

        void AddEdge(string fromRaw, string toRaw, string type, string evidence, string source = "manual")
        {
            var from = canonical.ResolveNodeId(fromRaw) ?? fromRaw;
            var to = canonical.ResolveNodeId(toRaw) ?? toRaw;
            if (string.Equals(from, to, StringComparison.Ordinal))
                return;

            var key = EdgeKey(from, to, type);
            if (!keys.Add(key))
                return;

            edges.Add(new TopologyEdge
            {
                From = from,
                To = to,
                Type = type,
                Evidence = evidence,
                Source = source,
                Confidence = 1.0,
                ValidFrom = DateTime.UtcNow,
                LastVerifiedAt = DateTime.UtcNow
            });
            EnsureNode(nodes, from, "manual");
            EnsureNode(nodes, to, "manual");
        }

        foreach (var relation in config.Relations)
        {
            if (string.IsNullOrWhiteSpace(relation.From) || string.IsNullOrWhiteSpace(relation.To))
                continue;

            AddEdge(relation.From, relation.To,
                string.IsNullOrWhiteSpace(relation.Type) ? "relatedTo" : relation.Type,
                relation.Note ?? "manual relation");
        }

        AddAccessRouteEdges(config, canonical, edges, keys);

        var invalidAssets = AssetRuntimeValidator.Validate(config).Issues
            .Where(i => i.Severity == AssetRuntimeIssueSeverity.Error)
            .Select(i => i.AssetId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        void AddRuntimeEdge(string fromId, string? targetId, string type, string evidence)
        {
            if (string.IsNullOrWhiteSpace(targetId) || invalidAssets.Contains(fromId))
                return;

            var from = canonical.ResolveNodeId(fromId);
            var to = canonical.ResolveNodeId(targetId);
            if (from is null || to is null)
                return;

            var key = EdgeKey(from, to, type);
            if (!keys.Add(key))
                return;

            edges.Add(new TopologyEdge
            {
                From = from,
                To = to,
                Type = type,
                Evidence = evidence,
                Source = "derived",
                Confidence = 1.0,
                ValidFrom = DateTime.UtcNow,
                LastVerifiedAt = DateTime.UtcNow
            });
        }

        foreach (var application in config.Applications)
        {
            switch (application.RuntimeKind)
            {
                case RuntimeEnvironmentKind.SshServer:
                    AddRuntimeEdge(AssetNode.App(application.Id),
                        AssetNode.Ssh(application.RuntimeSshAssetKey ?? string.Empty),
                        "runsOn",
                        $"config runtime: {application.Name} on ssh {application.RuntimeSshAssetKey}");
                    break;

                case RuntimeEnvironmentKind.DockerApplication:
                    AddRuntimeEdge(AssetNode.App(application.Id),
                        AssetNode.App(application.RuntimeDockerAssetKey ?? string.Empty),
                        "runsIn",
                        $"config runtime: {application.Name} in docker {application.RuntimeDockerAssetKey}");
                    break;
            }
        }

        foreach (var datasource in config.DataSources)
        {
            switch (datasource.RuntimeKind)
            {
                case RuntimeEnvironmentKind.SshServer:
                    AddRuntimeEdge(AssetNode.Ds(datasource.Id),
                        AssetNode.Ssh(datasource.RuntimeSshAssetKey ?? string.Empty),
                        "runsOn",
                        $"config runtime: {datasource.Name} on ssh {datasource.RuntimeSshAssetKey}");
                    break;

                case RuntimeEnvironmentKind.DockerApplication:
                    AddRuntimeEdge(AssetNode.Ds(datasource.Id),
                        AssetNode.App(datasource.RuntimeDockerAssetKey ?? string.Empty),
                        "runsIn",
                        $"config runtime: {datasource.Name} in docker {datasource.RuntimeDockerAssetKey}");
                    break;
            }
        }

        return new TopologyGraph
        {
            Nodes = nodes.Values.ToArray(),
            Edges = edges.ToArray(),
            Summary = new Dictionary<string, object?>
            {
                ["mode"] = "curated",
                ["logicalAssets"] = canonical.Assets.Count,
                ["manualRelations"] = config.Relations.Length,
                ["runtimeRelations"] = edges.Count(e => e.Type is "runsOn" or "runsIn"),
                ["accessRoutes"] = edges.Count(e => e.Type == "accessedVia")
            }
        };
    }

    public async Task<ServerDiscoveryEvidence?> GetServerDiscoveryEvidenceAsync(
        string serverId, CancellationToken ct = default)
    {
        if (_snapshotStore is null || string.IsNullOrWhiteSpace(serverId))
            return null;

        var snapshot = await _snapshotStore.GetLatestAsync(serverId, ct);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.DataJson))
            return null;

        var result = new ServerDiscoveryEvidence
        {
            ServerId = serverId,
            SnapshotId = snapshot.Id,
            CapturedAt = snapshot.CompletedAt ?? snapshot.CreatedAt
        };

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(snapshot.DataJson);
        }
        catch (JsonException ex)
        {
            result.Notes.Add($"快照 JSON 解析失败: {ex.Message}");
            return result;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("sections", out var sections) ||
                sections.ValueKind != JsonValueKind.Object)
            {
                result.Notes.Add("快照中没有 sections");
                return result;
            }

            if (sections.TryGetProperty("portmap", out var portmapSection))
            {
                var data = SectionData(portmapSection);
                if (data is { } portData &&
                    portData.TryGetProperty("listeners", out var listeners) &&
                    listeners.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in listeners.EnumerateArray().Take(200))
                    {
                        var port = EvidenceInt(row, "port");
                        if (port is null or <= 0)
                            continue;

                        var unit = EvidenceString(row, "unit");
                        result.Ports.Add(new DiscoveryPortEvidence
                        {
                            Protocol = EvidenceString(row, "proto") ?? "tcp",
                            Port = port.Value,
                            Bind = EvidenceString(row, "bind"),
                            Process = EvidenceString(row, "process"),
                            Pid = EvidenceInt(row, "pid"),
                            Unit = unit,
                            SystemdManaged = !string.IsNullOrWhiteSpace(unit),
                            Executable = EvidenceString(row, "exe"),
                            CommandLine = EvidenceString(row, "cmdline")
                        });
                    }

                    if (EvidenceBool(portData, "truncated"))
                        result.Notes.Add("端口快照已截断，证据可能不完整");
                }
            }

            if (sections.TryGetProperty("systemd", out var systemdSection))
            {
                var data = SectionData(systemdSection);
                if (data is { } systemdData &&
                    systemdData.TryGetProperty("units", out var units) &&
                    units.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in units.EnumerateArray().Take(200))
                    {
                        var unit = EvidenceString(row, "unit");
                        if (string.IsNullOrWhiteSpace(unit))
                            continue;

                        result.Services.Add(new DiscoveryServiceEvidence
                        {
                            Unit = unit,
                            Active = EvidenceString(row, "active"),
                            Sub = EvidenceString(row, "sub"),
                            Description = EvidenceString(row, "description")
                        });
                    }
                }
            }

            if (sections.TryGetProperty("docker", out var dockerSection))
            {
                var data = SectionData(dockerSection);
                if (data is { } dockerData &&
                    dockerData.TryGetProperty("containers", out var containers) &&
                    containers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in containers.EnumerateArray().Take(200))
                    {
                        var name = EvidenceString(row, "name") ?? EvidenceString(row, "id");
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        var labels = EvidenceString(row, "labels");
                        var (composeProject, composeService) = ParseComposeLabels(labels);
                        result.Containers.Add(new DiscoveryContainerEvidence
                        {
                            Name = name,
                            Image = EvidenceString(row, "image"),
                            State = EvidenceString(row, "state"),
                            Status = EvidenceString(row, "status"),
                            Ports = EvidenceString(row, "ports"),
                            ComposeProject = composeProject,
                            ComposeService = composeService,
                            Labels = labels
                        });
                    }
                }
            }

            if (sections.TryGetProperty("nginx_tls", out var nginxSection))
            {
                var data = SectionData(nginxSection);
                if (data is { } nginxData &&
                    nginxData.TryGetProperty("domains", out var domains) &&
                    domains.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in domains.EnumerateArray().Take(200))
                    {
                        var domain = EvidenceString(row, "domain");
                        if (string.IsNullOrWhiteSpace(domain) || domain == "_")
                            continue;

                        var evidence = new DiscoveryDomainEvidence
                        {
                            Domain = domain,
                            Ssl = EvidenceBool(row, "ssl"),
                            Ports = EvidenceIntArray(row, "ports")
                        };

                        if (row.TryGetProperty("certificates", out var certificates) &&
                            certificates.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var cert in certificates.EnumerateArray().Take(20))
                            {
                                evidence.Certificates.Add(new DiscoveryCertificateEvidence
                                {
                                    Path = EvidenceString(cert, "path"),
                                    SubjectCn = EvidenceString(cert, "subjectCn"),
                                    ExpiresAt = EvidenceString(cert, "notAfter"),
                                    Fingerprint = EvidenceString(cert, "fingerprint")
                                });
                            }
                        }

                        result.Domains.Add(evidence);
                    }

                    if (EvidenceBool(nginxData, "configTruncated"))
                        result.Notes.Add("nginx 配置已截断，站点/证书证据可能不完整");
                }
            }
        }

        return result;
    }

    private static JsonElement? SectionData(JsonElement section)
    {
        if (section.ValueKind != JsonValueKind.Object)
            return null;
        if (section.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String &&
            status.GetString() is "failed" or "skipped")
            return null;
        return section.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : null;
    }

    private static string? EvidenceString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? EvidenceInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static bool EvidenceBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value))
            return false;
        return value.ValueKind == JsonValueKind.True ||
               (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out var parsed) && parsed);
    }

    private static int[] EvidenceIntArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
            return Array.Empty<int>();

        return value.EnumerateArray()
            .Where(x => x.TryGetInt32(out _))
            .Select(x => x.GetInt32())
            .ToArray();
    }

    private static (string? Project, string? Service) ParseComposeLabels(string? labels)
    {
        if (string.IsNullOrWhiteSpace(labels))
            return (null, null);

        string? project = null;
        string? service = null;
        foreach (var pair in labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0)
                continue;

            var key = pair[..index].Trim();
            var value = pair[(index + 1)..].Trim();
            if (key.Equals("com.docker.compose.project", StringComparison.OrdinalIgnoreCase))
                project = value;
            else if (key.Equals("com.docker.compose.service", StringComparison.OrdinalIgnoreCase))
                service = value;
        }

        return (
            string.IsNullOrWhiteSpace(project) ? null : project,
            string.IsNullOrWhiteSpace(service) ? null : service);
    }
}
