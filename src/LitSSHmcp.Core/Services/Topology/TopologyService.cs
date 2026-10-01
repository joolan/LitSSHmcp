using System.Net;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Topology;

public interface ITopologyService
{
    Task<TopologyGraph> GetGraphAsync(CancellationToken ct = default);
    Task<DependencyGraph> GetDependenciesAsync(string assetId, CancellationToken ct = default);
    Task<DiscoveryResult> DiscoverAsync(string[]? serverIds, string[]? searchPaths, CancellationToken ct = default);
}

public class TopologyService : ITopologyService
{
    private const int MaxDepth = 6;

    private readonly IConfigService _configService;
    private readonly ISshService _sshService;
    private readonly IDatasourceDriverRegistry _driverRegistry;
    private readonly ITopologyStore _store;

    public TopologyService(
        IConfigService configService,
        ISshService sshService,
        IDatasourceDriverRegistry driverRegistry,
        ITopologyStore store)
    {
        _configService = configService;
        _sshService = sshService;
        _driverRegistry = driverRegistry;
        _store = store;
    }

    public async Task<TopologyGraph> GetGraphAsync(CancellationToken ct = default)
    {
        var config = await _configService.LoadConfigAsync();
        var nodes = new Dictionary<string, TopologyNode>(StringComparer.Ordinal);
        var edges = new List<TopologyEdge>();

        foreach (var server in config.Servers)
        {
            nodes[AssetNode.Ssh(server.Id)] = new TopologyNode
            {
                Id = AssetNode.Ssh(server.Id),
                Label = server.Name,
                Type = "ssh",
                Info = new Dictionary<string, object?>
                {
                    ["host"] = server.Host,
                    ["port"] = server.Port,
                    ["username"] = server.Username,
                    ["description"] = server.Description,
                    ["tags"] = server.Tags
                }
            };
        }

        foreach (var ds in config.DataSources)
        {
            nodes[AssetNode.Ds(ds.Id)] = new TopologyNode
            {
                Id = AssetNode.Ds(ds.Id),
                Label = ds.Name,
                Type = "datasource",
                Info = new Dictionary<string, object?>
                {
                    ["datasourceType"] = ds.Type,
                    ["host"] = ds.Host,
                    ["port"] = ds.Port,
                    ["username"] = ds.Username,
                    ["defaultDatabase"] = ds.DefaultDatabase,
                    ["accessMode"] = ds.AccessMode.ToString(),
                    ["description"] = ds.Description,
                    ["tags"] = ds.Tags
                }
            };
        }

        foreach (var app in config.Applications)
        {
            nodes[AssetNode.App(app.Id)] = new TopologyNode
            {
                Id = AssetNode.App(app.Id),
                Label = app.Name,
                Type = "application",
                Info = new Dictionary<string, object?>
                {
                    ["applicationType"] = app.Type,
                    ["host"] = app.Host,
                    ["port"] = app.Port,
                    ["description"] = app.Description,
                    ["tags"] = app.Tags
                }
            };
        }

        var manualKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in config.Relations)
        {
            if (string.IsNullOrWhiteSpace(rel.From) || string.IsNullOrWhiteSpace(rel.To)) continue;
            edges.Add(new TopologyEdge
            {
                From = rel.From,
                To = rel.To,
                Type = string.IsNullOrWhiteSpace(rel.Type) ? "relatedTo" : rel.Type,
                Evidence = rel.Note,
                Source = "manual"
            });
            manualKeys.Add(EdgeKey(rel.From, rel.To, rel.Type));
            EnsureNode(nodes, rel.From, "discovered");
            EnsureNode(nodes, rel.To, "discovered");
        }

        await _store.InitializeAsync();
        foreach (var edge in await _store.GetEdgesAsync())
        {
            if (manualKeys.Contains(EdgeKey(edge.From, edge.To, edge.Type))) continue;
            edges.Add(edge);
            EnsureNode(nodes, edge.From, "discovered");
            EnsureNode(nodes, edge.To, "discovered");
        }

        return new TopologyGraph
        {
            Nodes = nodes.Values.ToArray(),
            Edges = edges.ToArray(),
            Summary = new Dictionary<string, object?>
            {
                ["sshServers"] = config.Servers.Length,
                ["dataSources"] = config.DataSources.Length,
                ["applications"] = config.Applications.Length,
                ["manualRelations"] = config.Relations.Length,
                ["discoveredRelations"] = edges.Count(e => e.Source == "discovered")
            }
        };
    }

    public async Task<DependencyGraph> GetDependenciesAsync(string assetId, CancellationToken ct = default)
    {
        var graph = await GetGraphAsync(ct);
        var normalized = ResolveAssetId(graph, assetId);

        if (normalized == null)
        {
            return new DependencyGraph
            {
                Error = $"未找到资产: {assetId}。可用节点ID示例: " +
                        string.Join(", ", graph.Nodes.Take(10).Select(n => n.Id))
            };
        }

        var asset = graph.Nodes.First(n => n.Id == normalized);
        var upstreamNodes = Collect(graph, normalized, incoming: true);
        var downstreamNodes = Collect(graph, normalized, incoming: false);

        var relatedEdges = graph.Edges
            .Where(e => e.From == normalized || e.To == normalized ||
                        upstreamNodes.Any(n => n.Id == e.From || n.Id == e.To) ||
                        downstreamNodes.Any(n => n.Id == e.From || n.Id == e.To))
            .ToArray();

        return new DependencyGraph
        {
            Asset = asset,
            Upstream = upstreamNodes.ToArray(),
            Downstream = downstreamNodes.ToArray(),
            Edges = relatedEdges
        };
    }

    public async Task<DiscoveryResult> DiscoverAsync(string[]? serverIds, string[]? searchPaths, CancellationToken ct = default)
    {
        var config = await _configService.LoadConfigAsync();
        var result = new DiscoveryResult();
        var drafts = new List<TopologyEdge>();
        var notes = new List<string>();

        var servers = config.Servers
            .Where(s => serverIds == null || serverIds.Length == 0 ||
                        serverIds.Contains(s.Id) || serverIds.Contains(s.Name))
            .ToArray();

        if (servers.Length == 0)
            result.Errors = result.Errors.Append("未匹配到任何SSH服务器(检查 serverIds 参数)").ToArray();

        result.ScannedServers = servers.Select(s => $"{s.Name}({s.Host})").ToArray();

        var paths = searchPaths is { Length: > 0 }
            ? searchPaths
            : new[] { "/opt", "/home", "/srv", "/app", "/data" };
        var pathArg = string.Join(" ", paths.Select(p => p.Trim().TrimEnd('/')));

        var dnsCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            var sshNode = AssetNode.Ssh(server.Id);

            var pidMap = new Dictionary<long, DiscoveredApp>();

            var psResult = await _sshService.ExecuteCommandAsync(server,
                "ps -eo pid,user,args | grep -E '[j]ava.*\\.jar|[j]ava.*-jar ' | head -20", ct);
            if (psResult.Success)
            {
                foreach (var line in psResult.Output.Split('\n'))
                {
                    var match = Regex.Match(line.Trim(),
                        @"^(?<pid>\d+)\s+(?<user>\S+)\s+(?<args>.*)$");
                    if (!match.Success) continue;

                    var args = match.Groups["args"].Value;
                    var jarMatch = Regex.Match(args, @"(?<jar>[\w\-./]+\.jar)");
                    var jar = jarMatch.Success ? jarMatch.Groups["jar"].Value : null;
                    var name = jar != null
                        ? Path.GetFileNameWithoutExtension(jar)
                        : (Regex.Match(args, @"-jar\s+(?<n>[\w\-./]+)").Success
                            ? Path.GetFileNameWithoutExtension(Regex.Match(args, @"-jar\s+(?<n>[\w\-./]+)").Groups["n"].Value)
                            : "java-app");

                    var configured = config.Applications.FirstOrDefault(a =>
                        (!string.IsNullOrEmpty(a.Name) && name.Contains(a.Name, StringComparison.OrdinalIgnoreCase)) ||
                        name.Contains(a.Id, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(a.Name) && args.Contains(a.Name, StringComparison.OrdinalIgnoreCase)));

                    var app = new DiscoveredApp
                    {
                        SshNode = sshNode,
                        Pid = long.Parse(match.Groups["pid"].Value),
                        User = match.Groups["user"].Value,
                        Name = name,
                        JarPath = jar,
                        MatchedConfiguredApp = configured != null
                    };
                    pidMap[app.Pid] = app;
                    result.JavaProcesses = result.JavaProcesses.Append(app).ToArray();

                    if (configured != null)
                    {
                        drafts.Add(NewEdge(AssetNode.App(configured.Id), sshNode, "runsOn",
                            $"ps: pid={app.Pid} jar={jar}"));
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(psResult.Error))
            {
                notes.Add($"{server.Name}: java进程扫描失败: {Truncate(psResult.Error, 200)}");
            }

            // Docker 容器发现：匹配已登记的应用(ContainerName/名称)，自动建立 app -> ssh (runsOn)
            var dockerResult = await _sshService.ExecuteCommandAsync(server,
                "docker ps --no-trunc --format '{.Names}\t{.Image}\t{.Ports}\t{.Status}' 2>/dev/null", ct);
            if (dockerResult.Success && !string.IsNullOrWhiteSpace(dockerResult.Output) &&
                !dockerResult.Output.Contains("command not found", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var rawLine in dockerResult.Output.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("NAMES", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parts = line.Split('\t');
                    var containerName = parts[0].Trim();
                    if (containerName.Length == 0)
                        continue;

                    var image = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    var ports = parts.Length > 2 ? parts[2].Trim() : string.Empty;
                    var status = parts.Length > 3 ? parts[3].Trim() : string.Empty;

                    var configured = config.Applications.FirstOrDefault(a =>
                        (!string.IsNullOrEmpty(a.ContainerName) && a.ContainerName.Equals(containerName, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(a.Name) && (a.Name.Equals(containerName, StringComparison.OrdinalIgnoreCase) ||
                                                          containerName.Contains(a.Name, StringComparison.OrdinalIgnoreCase))));

                    result.DockerContainers = result.DockerContainers.Append(new DiscoveredContainer
                    {
                        SshNode = sshNode,
                        Name = containerName,
                        Image = image,
                        Ports = ports,
                        Status = status,
                        MatchedConfiguredApp = configured != null
                    }).ToArray();

                    if (configured != null)
                        drafts.Add(NewEdge(AssetNode.App(configured.Id), sshNode, "runsOn",
                            $"docker: {containerName} ({image})"));
                }
            }

            var ssResult = await _sshService.ExecuteCommandAsync(server,
                "(ss -Htnp state established 2>/dev/null || ss -tnp state established 2>/dev/null | grep -v '^State') | head -80", ct);
            if (ssResult.Success)
            {
                foreach (var line in ssResult.Output.Split('\n'))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5 || !line.Contains("ESTAB")) continue;

                    var local = parts[3];
                    var peer = parts[4];
                    var peerInfo = ParseAddress(peer);
                    if (peerInfo == null) continue;

                    foreach (var ds in config.DataSources)
                    {
                        if (peerInfo.Value.Port != ds.Port || !HostMatches(ds.Host, peerInfo.Value.Host, dnsCache))
                            continue;

                        var processInfo = parts.Length > 5
                            ? string.Join(' ', parts.Skip(5))
                            : null;
                        var evidence = $"ESTAB {local} -> {peer}" +
                                       (processInfo != null ? $" ({Truncate(processInfo, 120)})" : "");
                        drafts.Add(NewEdge(sshNode, AssetNode.Ds(ds.Id), "canAccess", evidence));

                        var pidMatch = processInfo == null
                            ? Match.Empty
                            : Regex.Match(processInfo, @"pid=(?<pid>\d+)");
                        if (pidMatch.Success &&
                            pidMap.TryGetValue(long.Parse(pidMatch.Groups["pid"].Value), out var app) &&
                            app.MatchedConfiguredApp)
                        {
                            var configuredApp = config.Applications.FirstOrDefault(a =>
                                nameMatches(a, app.Name));
                            if (configuredApp != null)
                                drafts.Add(NewEdge(AssetNode.App(configuredApp.Id), AssetNode.Ds(ds.Id),
                                    "connectsTo", $"ESTAB conn: {local} -> {peer}"));
                        }
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(ssResult.Error))
            {
                notes.Add($"{server.Name}: 连接扫描失败: {Truncate(ssResult.Error, 200)}");
            }

            var grepResult = await _sshService.ExecuteCommandAsync(server,
                $"grep -rE --include='application*.yml' --include='application*.yaml' --include='application*.properties' " +
                $"--include='bootstrap*.yml' --include='bootstrap*.properties' --include='logback*.xml' " +
                $"'jdbc:mysql://|redis://' {pathArg} 2>/dev/null | head -80", ct);

            if (grepResult.Success)
            {
                foreach (var line in grepResult.Output.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var colonIndex = line.IndexOf(':');
                    if (colonIndex <= 0) continue;
                    var filePath = line[..colonIndex];
                    var content = line[(colonIndex + 1)..];

                    foreach (Match m in Regex.Matches(content,
                                 @"jdbc:mysql://(?<host>[^:/?'\s]+)(?::(?<port>\d+))?/(?<db>[^?'\s""']+)"))
                    {
                        var dsHost = m.Groups["host"].Value;
                        var dsPort = m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value) : 3306;
                        var url = m.Value;

                        var matched = config.DataSources.FirstOrDefault(d =>
                            d.Port == dsPort && HostMatches(d.Host, dsHost, dnsCache));

                        if (matched == null)
                        {
                            result.UnmatchedEndpoints.Add(new Dictionary<string, object?>
                            {
                                ["server"] = server.Name,
                                ["file"] = filePath,
                                ["url"] = Truncate(url, 200),
                                ["note"] = "未匹配到已配置的数据源，建议核对后补充数据源配置"
                            });
                            continue;
                        }

                        var appName = InferAppName(filePath, config.Applications);
                        drafts.Add(NewEdge(sshNode, AssetNode.Ds(matched.Id), "canAccess",
                            $"config: {filePath} -> {Truncate(url, 160)}"));

                        if (appName != null)
                        {
                            var configuredApp = config.Applications.FirstOrDefault(a =>
                                a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase) ||
                                a.Id.Equals(appName, StringComparison.OrdinalIgnoreCase));
                            var appNode = configuredApp != null
                                ? AssetNode.App(configuredApp.Id)
                                : $"app:disc:{SanitizeId(appName)}";

                            drafts.Add(NewEdge(appNode, AssetNode.Ds(matched.Id), "connectsTo",
                                $"config: {filePath}"));
                            drafts.Add(NewEdge(appNode, sshNode, "runsOn", $"config: {filePath}"));
                        }
                    }

                    foreach (Match m in Regex.Matches(content, @"redis://(?<host>[^:/?'\s]+)(?::(?<port>\d+))?"))
                    {
                        var rHost = m.Groups["host"].Value;
                        var rPort = m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value) : 6379;
                        var matched = config.DataSources.FirstOrDefault(d =>
                            d.Port == rPort && HostMatches(d.Host, rHost, dnsCache));
                        if (matched != null)
                        {
                            drafts.Add(NewEdge(sshNode, AssetNode.Ds(matched.Id), "canAccess",
                                $"config: {filePath} -> {Truncate(m.Value, 160)}"));
                        }
                        else
                        {
                            result.UnmatchedEndpoints.Add(new Dictionary<string, object?>
                            {
                                ["server"] = server.Name,
                                ["file"] = filePath,
                                ["url"] = Truncate(m.Value, 200),
                                ["note"] = "未匹配到已配置的数据源(redis)"
                            });
                        }
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(grepResult.Error))
            {
                notes.Add($"{server.Name}: 配置文件扫描失败: {Truncate(grepResult.Error, 200)}");
            }
        }

        foreach (var ds in config.DataSources.Where(d => d.Type.Equals("mysql", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            var driver = _driverRegistry.Get(ds.Type);
            if (driver == null) continue;

            var processlist = await driver.QueryAsync(ds, "SHOW FULL PROCESSLIST", 500, ct);
            if (!processlist.Success)
            {
                notes.Add($"数据源 {ds.Name}: processlist 获取失败: {Truncate(processlist.Error ?? "", 200)}");
                continue;
            }

            var hostIndex = Array.IndexOf(processlist.Columns, "Host");
            var userIndex = Array.IndexOf(processlist.Columns, "User");
            if (hostIndex < 0) continue;

            var clientCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in processlist.Rows)
            {
                var hostValue = row.Values.ElementAt(hostIndex)?.ToString();
                if (string.IsNullOrWhiteSpace(hostValue)) continue;
                var clientHost = StripPort(hostValue);
                clientCounts[clientHost] = clientCounts.GetValueOrDefault(clientHost) + 1;
            }

            foreach (var (clientHost, count) in clientCounts)
            {
                var matchedServer = config.Servers.FirstOrDefault(s =>
                    HostMatches(s.Host, clientHost, dnsCache));
                var matchedApp = config.Applications.FirstOrDefault(a =>
                    !string.IsNullOrEmpty(a.Host) && HostMatches(a.Host, clientHost, dnsCache));

                if (matchedServer != null)
                {
                    drafts.Add(NewEdge(AssetNode.Ssh(matchedServer.Id), AssetNode.Ds(ds.Id), "canAccess",
                        $"mysql processlist: client={clientHost} sessions={count}"));
                }
                else if (matchedApp != null)
                {
                    drafts.Add(NewEdge(AssetNode.App(matchedApp.Id), AssetNode.Ds(ds.Id), "connectsTo",
                        $"mysql processlist: client={clientHost} sessions={count}"));
                }
                else
                {
                    result.UnmatchedMysqlClients.Add(new Dictionary<string, object?>
                    {
                        ["datasource"] = ds.Name,
                        ["clientHost"] = clientHost,
                        ["sessions"] = count,
                        ["note"] = "该客户端未匹配到已配置的服务器/应用，可用其与应用日志关联分析"
                    });
                }
            }
        }

        await _store.InitializeAsync();
        var existing = (await _store.GetEdgesAsync())
            .ToDictionary(e => EdgeKey(e.From, e.To, e.Type), StringComparer.Ordinal);

        var newEdges = new List<TopologyEdge>();
        var updatedEdges = new List<TopologyEdge>();

        foreach (var draft in drafts
                     .GroupBy(e => EdgeKey(e.From, e.To, e.Type))
                     .Select(g => g.Last()))
        {
            if (existing.ContainsKey(EdgeKey(draft.From, draft.To, draft.Type)))
            {
                updatedEdges.Add(draft);
            }
            else
            {
                newEdges.Add(draft);
                draft.Source = "discovered";
            }
            await _store.UpsertEdgeAsync(draft);
        }

        result.NewEdges = newEdges.ToArray();
        result.UpdatedEdges = updatedEdges.ToArray();
        result.Notes = notes.Append(
            "扫描结果为证据快照: 新增边已写入拓扑缓存, 可用 topology_get_overview 查看合并后的完整拓扑").ToArray();

        return result;

        static bool nameMatches(ApplicationConfig app, string discoveredName) =>
            discoveredName.Contains(app.Name, StringComparison.OrdinalIgnoreCase) ||
            app.Name.Contains(discoveredName, StringComparison.OrdinalIgnoreCase) ||
            discoveredName.Contains(app.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static TopologyEdge NewEdge(string from, string to, string type, string evidence) => new()
    {
        From = from,
        To = to,
        Type = type,
        Evidence = evidence,
        Source = "discovered",
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow
    };

    private static void EnsureNode(Dictionary<string, TopologyNode> nodes, string id, string source)
    {
        if (nodes.ContainsKey(id)) return;
        var (type, label) = id switch
        {
            _ when id.StartsWith(AssetNode.SshPrefix, StringComparison.Ordinal) => ("ssh", id[4..]),
            _ when id.StartsWith(AssetNode.DsPrefix, StringComparison.Ordinal) => ("datasource", id[3..]),
            _ when id.StartsWith(AssetNode.AppPrefix, StringComparison.Ordinal) => ("application", id[4..]),
            _ => ("unknown", id)
        };
        nodes[id] = new TopologyNode
        {
            Id = id,
            Label = label,
            Type = type,
            Source = source
        };
    }

    private static List<TopologyNode> Collect(TopologyGraph graph, string assetId, bool incoming)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { assetId };
        var result = new List<TopologyNode>();
        var frontier = new List<string> { assetId };

        for (var depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                foreach (var edge in graph.Edges)
                {
                    string? neighbor = incoming
                        ? (edge.To == current ? edge.From : null)
                        : (edge.From == current ? edge.To : null);
                    if (neighbor == null || !visited.Add(neighbor)) continue;

                    var node = graph.Nodes.FirstOrDefault(n => n.Id == neighbor);
                    if (node != null) result.Add(node);
                    next.Add(neighbor);
                }
            }
            frontier = next;
        }

        return result;
    }

    private static string? ResolveAssetId(TopologyGraph graph, string assetId)
    {
        if (graph.Nodes.Any(n => n.Id == assetId)) return assetId;

        return graph.Nodes.FirstOrDefault(n =>
            n.Id.EndsWith(":" + assetId, StringComparison.Ordinal) ||
            n.Label.Equals(assetId, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static string EdgeKey(string from, string to, string type) => $"{from}|{to}|{type}";

    private static (string Host, int Port)? ParseAddress(string address)
    {
        var match = Regex.Match(address, @"^(?:\[(?<v6>[^\]]+)\]|(?<host>[^:]+)):(?<port>\d+)$");
        if (!match.Success) return null;
        var host = match.Groups["v6"].Success ? match.Groups["v6"].Value : match.Groups["host"].Value;
        if (!int.TryParse(match.Groups["port"].Value, out var port)) return null;
        return (host, port);
    }

    private static bool HostMatches(string configuredHost, string actualHost, Dictionary<string, string[]> dnsCache)
    {
        if (configuredHost.Equals(actualHost, StringComparison.OrdinalIgnoreCase)) return true;

        if (IPAddress.TryParse(configuredHost, out var configuredIp) &&
            IPAddress.TryParse(actualHost, out var actualIp))
            return configuredIp.Equals(actualIp);

        try
        {
            if (!dnsCache.TryGetValue(configuredHost, out var addresses))
            {
                addresses = Dns.GetHostAddresses(configuredHost)
                    .Select(a => a.ToString())
                    .ToArray();
                dnsCache[configuredHost] = addresses;
            }
            return addresses.Any(a => a.Equals(actualHost, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string StripPort(string hostWithPort)
    {
        var match = Regex.Match(hostWithPort, @"^(?<host>[^:]+):\d+$");
        return match.Success ? match.Groups["host"].Value : hostWithPort;
    }

    private static string? InferAppName(string filePath, ApplicationConfig[] applications)
    {
        foreach (var app in applications)
        {
            if (!string.IsNullOrEmpty(app.Name) &&
                filePath.Contains(app.Name, StringComparison.OrdinalIgnoreCase))
                return app.Name;
        }

        var segments = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        var dir = segments[^2];
        if (dir is "config" or "conf" or "properties" && segments.Length >= 3)
            dir = segments[^3];

        return dir is "." or ".." ? null : dir;
    }

    private static string SanitizeId(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9\-_.]", "-");

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
