using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Topology;

public interface ITopologyService
{
    Task<TopologyGraph> GetGraphAsync(CancellationToken ct = default);
    Task<DependencyGraph> GetDependenciesAsync(string assetId, CancellationToken ct = default);
    Task<DiscoveryResult> DiscoverAsync(string[]? serverIds, string[]? searchPaths, CancellationToken ct = default);
}

/// <summary>已有自动发现在执行时抛出（用于节流：同一时间只允许一个发现任务）。</summary>
public sealed class DiscoveryInProgressException : Exception
{
    public DiscoveryInProgressException() : base("已有自动发现在执行，请稍后重试") { }
}

public class TopologyService : ITopologyService
{
    private const int MaxDepth = 6;

    private readonly IConfigService _configService;
    private readonly ISshService _sshService;
    private readonly IDatasourceDriverRegistry _driverRegistry;
    private readonly ITopologyStore _store;
    private readonly ICommandFilterService _commandFilter;
    private readonly IAuditLogService _auditLogService;
    private readonly SemaphoreSlim _discoverGate = new(1, 1);
    private bool _auditReady;

    public TopologyService(
        IConfigService configService,
        ISshService sshService,
        IDatasourceDriverRegistry driverRegistry,
        ITopologyStore store,
        ICommandFilterService commandFilter,
        IAuditLogService auditLogService)
    {
        _configService = configService;
        _sshService = sshService;
        _driverRegistry = driverRegistry;
        _store = store;
        _commandFilter = commandFilter;
        _auditLogService = auditLogService;
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
                    ["tags"] = server.Tags,
                    ["disabled"] = server.Disabled
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

        // 合并发现节点的附加信息（如监听端口），并在待确认节点标签上显示端口
        foreach (var (nodeId, json) in await _store.GetNodeInfosAsync())
        {
            if (!nodes.TryGetValue(nodeId, out var node)) continue;

            try
            {
                if (JsonSerializer.Deserialize<Dictionary<string, object?>>(json) is { } info)
                    foreach (var kv in info)
                        node.Info[kv.Key] = kv.Value;
            }
            catch { /* 忽略损坏的 info */ }

            if (nodeId.Contains(":disc:", StringComparison.Ordinal) &&
                node.Info.TryGetValue("ports", out var portsVal) &&
                portsVal is string portsText && portsText.Length > 0 &&
                !node.Label.Contains(":" + portsText, StringComparison.Ordinal))
                node.Label = node.Label.Replace(" (待确认)", $" :{portsText} (待确认)");
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

    /// <summary>
    /// 自动发现。会做**节流**：同一时间只允许一个发现任务，正在执行时本次调用直接返回
    /// <see cref="DiscoveryInProgressException"/>（避免并发扫描拖垮目标机与本机）。
    /// </summary>
    public async Task<DiscoveryResult> DiscoverAsync(string[]? serverIds, string[]? searchPaths, CancellationToken ct = default)
    {
        if (!await _discoverGate.WaitAsync(0, ct))
            throw new DiscoveryInProgressException();

        try
        {
            return await DiscoverCoreAsync(serverIds, searchPaths, ct);
        }
        finally
        {
            _discoverGate.Release();
        }
    }

    private async Task<DiscoveryResult> DiscoverCoreAsync(string[]? serverIds, string[]? searchPaths, CancellationToken ct = default)
    {
        var config = await _configService.LoadConfigAsync();
        var result = new DiscoveryResult();
        var drafts = new List<TopologyEdge>();
        var notes = new List<string>();

        // 每次重新发现是"本次扫描的快照"：先清掉上一轮所有"待确认"节点/边，再由本次证据重建。
        await _store.InitializeAsync();
        await _store.ClearPendingAsync();

        // 是否用提权跑部分只读探测（监听端口/配置扫描）——仅对该服务器配置了 SudoType 时生效
        var useSudo = config.Security?.Discovery?.UseSudo ?? false;

        var servers = config.Servers
            .Where(s => serverIds == null || serverIds.Length == 0 ||
                        serverIds.Contains(s.Id) || serverIds.Contains(s.Name))
            .ToArray();

        // 禁用的服务器一律不连接（与 MCP 工具层的 server_disabled 闸门一致：发现同样不能"放行"）
        var disabled = servers.Where(s => s.Disabled).ToArray();
        if (disabled.Length > 0)
        {
            notes.Add($"已跳过 {disabled.Length} 台已禁用的服务器: {string.Join(", ", disabled.Select(s => s.Name))}" +
                      "（如需扫描请先在桌面 App 启用）");
            servers = servers.Where(s => !s.Disabled).ToArray();
        }

        if (servers.Length == 0)
            result.Errors = result.Errors.Append(
                disabled.Length > 0
                    ? $"匹配到的 SSH 服务器全部处于禁用状态({string.Join(", ", disabled.Select(s => s.Name))}), 已拒绝扫描"
                    : "未匹配到任何SSH服务器(检查 serverIds 参数)").ToArray();

        result.ScannedServers = servers.Select(s => $"{s.Name}({s.Host})").ToArray();

        // 扫描路径只接受两处来源: ① 调用方显式指定(已由 PathPolicy 校验); ② 配置的 allowedSearchPaths。
        // 不再回退到硬编码目录, 否则"限定扫描路径"这一安全配置会被静默架空。
        var allowedPaths = config.Security?.Discovery?.AllowedSearchPaths ?? Array.Empty<string>();
        var paths = (searchPaths is { Length: > 0 } ? searchPaths : allowedPaths)
            .Select(p => p.Trim().TrimEnd('/'))
            .Where(p => p.Length > 0)
            .ToArray();

        if (paths.Length == 0)
            notes.Add("未配置允许的扫描路径(security.discovery.allowedSearchPaths), 已跳过配置文件扫描");

        var scanCommand = paths.Length > 0 ? BuildConfigScanCommand(paths) : null;
        if (scanCommand != null && _commandFilter.CheckCommand(scanCommand) == CommandFilterResult.Blocked)
        {
            notes.Add("配置文件扫描被安全策略拦截(扫描路径中含被禁止的命令片段), 已跳过该步骤");
            scanCommand = null;
        }

        var dnsCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            var sshNode = AssetNode.Ssh(server.Id);

            var pidMap = new Dictionary<long, DiscoveredApp>();

            var psResult = await ProbeAsync(server,
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
                    else if (jar != null)
                    {
                        // 未登记的应用也建"待确认"节点，让发现结果有料（可在资产关系里删除/补登）
                        var pendingApp = PendingAppId(name);
                        drafts.Add(NewEdge(pendingApp, sshNode, "runsOn",
                            $"ps(未登记应用): pid={app.Pid} jar={jar}"));
                        await _store.UpsertNodeInfoAsync(pendingApp, NodeInfoJson("java", null, null, jar));
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(psResult.Error))
            {
                notes.Add($"{server.Name}: java进程扫描失败: {Truncate(psResult.Error, 200)}");
            }

            // Docker 容器发现：匹配已登记的应用(ContainerName/名称)，自动建立 app -> ssh (runsOn)
            var dockerResult = await ProbeAsync(server,
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
                    else
                        drafts.Add(NewEdge(PendingAppId(containerName), sshNode, "runsOn",
                            $"docker(未登记应用): {containerName} ({image}) {status}".Trim()));
                }
            }

            // 监听端口（尽量拿到 pid/进程名 → 端口；非 root 时进程名可能隐藏，但端口列表仍可获取）
            var listenResult = await ProbeAsync(server, "ss -Hltnp 2>/dev/null || ss -Hltn 2>/dev/null", ct, useSudo);
            var portsByPid = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            var portsByComm = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            var allListenPorts = new HashSet<int>();
            if (listenResult.Success)
                ParseListeningPorts(listenResult.Output, portsByPid, portsByComm, allListenPorts);

            // 通用服务进程扫描：按 comm(进程名) 精准匹配（不做 grep 子串匹配，避免 user/args 里的关键字造成大量误报）；
            // 归一化后每类服务只建一个节点（mysqld/mysqld_safe→mysql, redis-server→redis 等）。
            var svcResult = await ProbeAsync(server,
                "ps -eo pid,user,comm,args --no-headers 2>/dev/null | head -400", ct);
            if (svcResult.Success)
            {
                var seenService = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in svcResult.Output.Split('\n'))
                {
                    var t = line.Trim().Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length < 3) continue;

                    var pid = t[0];
                    var comm = t[2];
                    var args = t.Length > 3 ? t[3] : string.Empty;
                    var service = NormalizeServiceName(comm);
                    if (service == null || !seenService.Add(service)) continue;

                    var ports = ResolveServicePorts(pid, comm, service, args, portsByPid, portsByComm, allListenPorts);
                    var portText = ports.Count > 0 ? $" ports={string.Join(",", ports.OrderBy(x => x))}" : string.Empty;

                    if (DataServiceNames.Contains(service))
                    {
                        // 数据服务(mysql/redis/postgres...) → 数据源节点；已配置同类型数据源则直接连真实节点
                        var dsType = DataServiceDsType(service);
                        var candidate = dsType == null ? null : config.DataSources.FirstOrDefault(d =>
                            d.Type.Equals(dsType, StringComparison.OrdinalIgnoreCase) &&
                            (HostMatches(d.Host, server.Host, dnsCache) || IsLocalHost(d.Host)));

                        var dsNode = candidate != null ? AssetNode.Ds(candidate.Id) : $"ds:disc:{service}";
                        drafts.Add(NewEdge(sshNode, dsNode, "canAccess",
                            $"process: pid={pid} comm={comm}{portText}" + (candidate == null ? " (未登记数据源)" : "")));
                        await _store.UpsertNodeInfoAsync(dsNode, NodeInfoJson(dsType ?? service, null, ports));
                    }
                    else
                    {
                        // 应用类服务(nginx/php-fpm/haproxy...) → 应用节点
                        var configured = config.Applications.FirstOrDefault(a => nameMatches(a, service) || nameMatches(a, comm));
                        var appNode = configured != null ? AssetNode.App(configured.Id) : PendingAppId(service);
                        drafts.Add(NewEdge(appNode, sshNode, "runsOn",
                            $"process: pid={pid} comm={comm}{portText}" + (configured == null ? " (未登记应用)" : "")));
                        await _store.UpsertNodeInfoAsync(appNode, NodeInfoJson(service, null, ports, ExtractPathFromArgs(args)));
                    }
                }
            }

            // 服务器节点的监听端口（整体概览）
            if (allListenPorts.Count > 0)
                await _store.UpsertNodeInfoAsync(sshNode, NodeInfoJson(null, null, allListenPorts));

            var ssResult = await ProbeAsync(server,
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

            var grepResult = scanCommand == null ? null : await ProbeAsync(server, scanCommand, ct, useSudo);

            if (grepResult != null && grepResult.Success)
            {
                // 把配置里发现的数据库/缓存端点归为边：匹配到已配置数据源则连真实节点，
                // 否则建一个"待确认"节点(ds:disc:...)，让发现结果有料（而不是只报告未匹配）。
                // 配置里的端点是否有“落地证据”: 远程主机直接保留; 本机(localhost/本机地址/指向本服务器的域名)
                // 必须确有端口在监听, 否则视为示例/过期配置, 不建“待确认”节点
                // —— 避免“服务器上没有 5672/6379 监听, 却冒出 amqps/redis 待确认节点”这种误报。
                bool LocalEndpointConfirmed(string host, int port)
                {
                    if (IsPlaceholderHost(host)) return false;
                    var isLocal = IsLocalHost(host)
                        || HostMatches(server.Host, host, dnsCache)
                        || HostMatches(host, server.Host, dnsCache);
                    if (!isLocal) return true;
                    if (!listenResult.Success) return true; // 端口扫描不可用, 宁可不误杀
                    return allListenPorts.Contains(port);
                }

                void AddDbEndpoint(string host, int port, string url, string filePath, string kind)
                {
                    var matched = config.DataSources.FirstOrDefault(d =>
                        d.Port == port &&
                        d.Type.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
                        HostMatches(d.Host, host, dnsCache));

                    if (matched == null)
                    {
                        if (!LocalEndpointConfirmed(host, port))
                        {
                            notes.Add($"{server.Name}: 配置发现 {kind} 端点 {host}:{port} 但本机未监听该端口, 已跳过({Truncate(filePath, 120)})");
                            return;
                        }

                        result.UnmatchedEndpoints.Add(new Dictionary<string, object?>
                        {
                            ["server"] = server.Name,
                            ["file"] = filePath,
                            ["url"] = Truncate(url, 200),
                            ["note"] = $"未匹配到已配置的数据源({kind})，建议核对后补充数据源配置"
                        });
                        drafts.Add(NewEdge(sshNode, PendingDsId(host, port), "canAccess",
                            $"config(未配置数据源): {filePath} -> {Truncate(url, 160)}"));
                        return;
                    }

                    drafts.Add(NewEdge(sshNode, AssetNode.Ds(matched.Id), "canAccess",
                        $"config: {filePath} -> {Truncate(url, 160)}"));

                    var appName = InferAppName(filePath, config.Applications);
                    if (appName == null) return;

                    var appNode = AppNodeForName(appName, config);
                    drafts.Add(NewEdge(appNode, AssetNode.Ds(matched.Id), "connectsTo",
                        $"config: {filePath}"));
                    drafts.Add(NewEdge(appNode, sshNode, "runsOn", $"config: {filePath}"));
                }

                // nginx proxy_pass http(s)://<目标> → 推断的 nginx 应用 connectsTo 目标（配置应用或待确认节点）
                void AddAppEndpoint(string target, string filePath, string kind)
                {
                    var host = target;
                    int? port = null;
                    var idx = target.LastIndexOf(':');
                    if (idx > 0 && int.TryParse(target[(idx + 1)..], out var p))
                    {
                        host = target[..idx];
                        port = p;
                    }

                    var configuredTarget = port is int pp
                        ? config.Applications.FirstOrDefault(a => a.Port == pp)
                        : null;
                    var targetNode = configuredTarget != null
                        ? AssetNode.App(configuredTarget.Id)
                        : PendingAppId(port is int p2 ? $"{host}-{p2}" : host);

                    var sourceApp = InferAppName(filePath, config.Applications);
                    var sourceNode = sourceApp != null ? AppNodeForName(sourceApp, config) : PendingAppId("nginx");

                    drafts.Add(NewEdge(sourceNode, targetNode, "connectsTo",
                        $"{kind}: {filePath} -> {Truncate(target, 120)}"));
                }

                // MQ 端点(RabbitMQ/Kafka) → 推断的应用 connectsTo mq:disc:<host>-<port>
                void AddMqEndpoint(string host, int port, string url, string filePath, string kind)
                {
                    if (!LocalEndpointConfirmed(host, port))
                    {
                        notes.Add($"{server.Name}: 配置发现 {kind} 端点 {host}:{port} 但本机未监听该端口, 已跳过({Truncate(filePath, 120)})");
                        return;
                    }

                    var sourceApp = InferAppName(filePath, config.Applications);
                    var sourceNode = sourceApp != null ? AppNodeForName(sourceApp, config) : sshNode;
                    drafts.Add(NewEdge(sourceNode, $"mq:disc:{SanitizeId(host)}-{port}", "connectsTo",
                        $"{kind}: {filePath} -> {Truncate(url, 120)}"));
                }

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
                        var host = m.Groups["host"].Value;
                        var port = m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value) : 3306;
                        AddDbEndpoint(host, port, m.Value, filePath, "mysql");
                    }

                    foreach (Match m in Regex.Matches(content,
                                 @"jdbc:postgresql://(?<host>[^:/?'\s]+)(?::(?<port>\d+))?/(?<db>[^?'\s""']+)"))
                    {
                        var host = m.Groups["host"].Value;
                        var port = m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value) : 5432;
                        AddDbEndpoint(host, port, m.Value, filePath, "postgres");
                    }

                    foreach (Match m in Regex.Matches(content, @"redis://(?<host>[^:/?'\s]+)(?::(?<port>\d+))?"))
                    {
                        var host = m.Groups["host"].Value;
                        var port = m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value) : 6379;
                        AddDbEndpoint(host, port, m.Value, filePath, "redis");
                    }

                    // nginx: upstream <name> { ... } → 该 upstream 组视为该服务器上的"待确认"应用
                    foreach (Match m in Regex.Matches(content, @"\bupstream\s+(?<name>[A-Za-z0-9_.\-]+)"))
                        drafts.Add(NewEdge(PendingAppId(m.Groups["name"].Value), sshNode, "runsOn",
                            $"nginx upstream: {filePath}"));

                    // nginx: proxy_pass http(s)://<目标>[/...]
                    foreach (Match m in Regex.Matches(content, @"proxy_pass\s+https?://(?<target>[^\s;/'""]+)"))
                        AddAppEndpoint(m.Groups["target"].Value, filePath, "nginx");

                    // RabbitMQ: amqp(s)://[user:pass@]host[:port]（amqps 默认 5671, amqp 默认 5672）
                    foreach (Match m in Regex.Matches(content, @"(?<![A-Za-z0-9_])(?<scheme>amqps?)://(?:[^@/\s]*@)?(?<host>[^:/?'\s]+)(?::(?<port>\d+))?"))
                    {
                        var host = m.Groups["host"].Value;
                        var scheme = m.Groups["scheme"].Value;
                        var defaultPort = string.Equals(scheme, "amqps", StringComparison.OrdinalIgnoreCase) ? 5671 : 5672;
                        var port = m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value) : defaultPort;
                        AddMqEndpoint(host, port, m.Value, filePath, "rabbitmq");
                    }

                    // Kafka: bootstrap.servers / bootstrap-servers = host:port[,host:port]
                    foreach (Match m in Regex.Matches(content,
                                 @"bootstrap[.\-]servers\s*[:=]\s*(?<list>[A-Za-z0-9_.\-]+:\d+(?:\s*,\s*[A-Za-z0-9_.\-]+:\d+)*)"))
                    {
                        foreach (var hp in m.Groups["list"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var idx = hp.LastIndexOf(':');
                            if (idx > 0 && int.TryParse(hp[(idx + 1)..], out var kport))
                                AddMqEndpoint(hp[..idx], kport, $"kafka://{hp}", filePath, "kafka");
                        }
                    }
                }
            }
            else if (grepResult != null && !string.IsNullOrWhiteSpace(grepResult.Error))
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
                else if (IsLocalHost(clientHost))
                {
                    // 本机(localhost/127.0.0.1)客户端应归属到该数据源所在的服务器，而不是另建 localhost 节点
                    var hostServer = config.Servers.FirstOrDefault(s => HostMatches(s.Host, ds.Host, dnsCache))
                        ?? config.Servers.FirstOrDefault(s => !string.IsNullOrEmpty(ds.TunnelServerId) && s.Id == ds.TunnelServerId);
                    if (hostServer != null)
                        drafts.Add(NewEdge(AssetNode.Ssh(hostServer.Id), AssetNode.Ds(ds.Id), "canAccess",
                            $"mysql processlist(本机): client={clientHost} sessions={count}"));
                    else
                        notes.Add($"数据源 {ds.Name}: 本机客户端({clientHost})，但未定位到其所在服务器，未建关系");
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
                    // 未登记的客户端也建"待确认"服务器节点，使图谱能反映真实访问来源
                    drafts.Add(NewEdge($"ssh:disc:{SanitizeId(clientHost)}", AssetNode.Ds(ds.Id), "canAccess",
                        $"mysql processlist(未登记客户端): client={clientHost} sessions={count}"));
                }
            }
        }

        await _store.InitializeAsync();
        var existing = (await _store.GetEdgesAsync())
            .ToDictionary(e => EdgeKey(e.From, e.To, e.Type), StringComparer.Ordinal);

        // 人工声明的关系不再重复写入/计入发现结果（避免 NewEdges 把人工边也算 "new"）
        var manualKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in config.Relations)
        {
            if (!string.IsNullOrWhiteSpace(rel.From) && !string.IsNullOrWhiteSpace(rel.To))
                manualKeys.Add(EdgeKey(rel.From, rel.To, EffectiveType(rel.Type)));
        }

        var newEdges = new List<TopologyEdge>();
        var updatedEdges = new List<TopologyEdge>();

        foreach (var draft in drafts
                     .GroupBy(e => EdgeKey(e.From, e.To, e.Type))
                     .Select(g => g.Last()))
        {
            var key = EdgeKey(draft.From, draft.To, draft.Type);
            if (manualKeys.Contains(key))
                continue; // 人工已声明，交给人工关系承载

            if (existing.ContainsKey(key))
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
            "扫描结果为证据快照: 新增边已写入拓扑缓存, 可用 topology_get_overview 查看合并后的完整拓扑; " +
            "未登记的应用/数据源/客户端会以 app:disc:* / ds:disc:* / ssh:disc:* 的\"待确认\"节点出现(可在资产关系里删除)").ToArray();

        return result;

        static bool nameMatches(ApplicationConfig app, string discoveredName) =>
            discoveredName.Contains(app.Name, StringComparison.OrdinalIgnoreCase) ||
            app.Name.Contains(discoveredName, StringComparison.OrdinalIgnoreCase) ||
            discoveredName.Contains(app.Id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 构造配置文件扫描命令。所有路径都经 <see cref="ShellQuote.Single"/> 单引号转义，
    /// 使 <c>; | &amp; $( ) `</c> 等 shell 元字符退化为字面量（防注入的唯一入口，勿绕过）。
    /// </summary>
    public static string BuildConfigScanCommand(IEnumerable<string> paths) =>
        "grep -rE --include='application*.yml' --include='application*.yaml' --include='application*.properties' " +
        "--include='bootstrap*.yml' --include='bootstrap*.properties' --include='logback*.xml' --include='*.conf' " +
        $"'jdbc:mysql://|jdbc:postgresql://|redis://|amqp://|amqps://|kafka://|bootstrap[.-]servers|proxy_pass|upstream' " +
        $"{ShellQuote.Join(paths)} 2>/dev/null | head -120";

    /// <summary>
    /// 执行拓扑探测命令：先过 CommandFilter（被拦截则不下发），执行后落审计。
    /// 拓扑发现此前完全绕过命令过滤与审计，属于安全旁路，现统一从这里走。
    /// </summary>
    private async Task<CommandResult> ProbeAsync(SshServerConfig server, string command, CancellationToken ct, bool useSudo = false)
    {
        if (_commandFilter.CheckCommand(command) == CommandFilterResult.Blocked)
        {
            await AuditAsync(server, command, CommandStatus.Blocked, null, null);
            return new CommandResult
            {
                Success = false,
                Error = "blocked_command: 命令被安全策略禁止执行",
                ExitCode = -1
            };
        }

        // 需要提权时走 sudo 路径（仅当该服务器配置了 SudoType）；提权密码由服务端注入、不暴露给 AI。
        var result = useSudo && server.SudoType != SudoType.None
            ? await _sshService.ExecuteWithSudoAsync(server, command, ct)
            : await _sshService.ExecuteCommandAsync(server, command, ct);

        await AuditAsync(server, command,
            result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            result.Output, result.ExitCode);
        return result;
    }

    /// <summary>
    /// 审计写入为"尽力而为"：命令已在远端执行，审计失败绝不能让工具整体报错，
    /// 否则模型看到失败后重试会造成重复执行。首次写入会懒初始化审计库（App 侧与启动降级场景）。
    /// </summary>
    private async Task AuditAsync(SshServerConfig server, string command, CommandStatus status, string? output, int? exitCode)
    {
        var log = new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = command,
            Result = output == null ? null : Truncate(output, 4000),
            Status = status,
            ExitCode = exitCode
        };

        try
        {
            if (!_auditReady)
            {
                await _auditLogService.InitializeAsync();
                _auditReady = true;
            }
            await _auditLogService.LogCommandAsync(log);
            return;
        }
        catch
        {
            _auditReady = false;
        }

        try { await _auditLogService.LogCommandAsync(log); }
        catch { /* 审计失败不影响主流程 */ }
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
            _ when id.StartsWith(AssetNode.MqPrefix, StringComparison.Ordinal) => ("mq", id[3..]),
            _ => ("unknown", id)
        };

        if (label.StartsWith("disc:", StringComparison.Ordinal))
            label = label[5..] + " (待确认)";

        nodes[id] = new TopologyNode
        {
            Id = id,
            Label = label,
            Type = type,
            Source = source
        };
    }

    private static string EffectiveType(string? type) =>
        string.IsNullOrWhiteSpace(type) ? "relatedTo" : type.Trim();

    /// <summary>未登记应用的"待确认"节点 ID。</summary>
    private static string PendingAppId(string name) => $"app:disc:{SanitizeId(name)}";

    /// <summary>未配置数据源的"待确认"节点 ID。</summary>
    private static string PendingDsId(string host, int port) => $"ds:disc:{SanitizeId(host)}-{port}";

    /// <summary>把推断出的应用名映射为节点：已登记则用真实应用节点，否则用"待确认"节点。</summary>
    private static string AppNodeForName(string appName, AppConfig config)
    {
        var configured = config.Applications.FirstOrDefault(a =>
            a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase) ||
            a.Id.Equals(appName, StringComparison.OrdinalIgnoreCase));
        return configured != null ? AssetNode.App(configured.Id) : PendingAppId(appName);
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

    /// <summary>本机地址(localhost/127.x/::1/0.0.0.0)，用于归属到当前 SSH 服务器而不是另建节点。</summary>
    private static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().Trim('[', ']').ToLowerInvariant();
        return h is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0" or "localhost.localdomain"
            || h.StartsWith("127.", StringComparison.Ordinal);
    }

    /// <summary>配置里的主机名是否为占位符/变量(如 ${RABBIT_HOST}、&lt;host&gt;)，这类不应生成发现节点。</summary>
    private static bool IsPlaceholderHost(string host) =>
        string.IsNullOrWhiteSpace(host)
        || host.IndexOfAny(new[] { '$', '{', '}', '%', '<', '>' }) >= 0;

    // 只识别明确的服务守护进程名(comm)，避免按 user/args 子串匹配造成的海量误报
    private static readonly Dictionary<string, string> ServiceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nginx"] = "nginx",
        ["httpd"] = "httpd",
        ["apache2"] = "apache2",
        ["mysqld"] = "mysql",
        ["mysqld_safe"] = "mysql",
        ["mariadbd"] = "mariadb",
        ["mariadb"] = "mariadb",
        ["redis-server"] = "redis",
        ["redis"] = "redis",
        ["postgres"] = "postgres",
        ["postmaster"] = "postgres",
        ["mongod"] = "mongod",
        ["memcached"] = "memcached",
        ["haproxy"] = "haproxy",
        ["varnishd"] = "varnish",
        ["php-fpm"] = "php-fpm",
        ["gunicorn"] = "gunicorn",
        ["uwsgi"] = "uwsgi",
        ["supervisord"] = "supervisord",
        ["rabbitmq"] = "rabbitmq",
        ["named"] = "dns",
    };

    private static readonly HashSet<string> DataServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mysql", "mariadb", "redis", "postgres", "mongod", "memcached"
    };

    private static string? NormalizeServiceName(string comm) =>
        ServiceAliases.TryGetValue(comm.Trim(), out var name) ? name : null;

    private static string? DataServiceDsType(string service) => service.ToLowerInvariant() switch
    {
        "mysql" => "mysql",
        "mariadb" => "mysql",
        "redis" => "redis",
        "postgres" => "postgres",
        _ => null
    };

    /// <summary>节点附加信息 JSON：type / host / ports / path。</summary>
    private static string NodeInfoJson(string? type, string? host, IEnumerable<int>? ports, string? path = null)
    {
        var info = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(type)) info["type"] = type;
        if (!string.IsNullOrWhiteSpace(host)) info["host"] = host;
        if (!string.IsNullOrWhiteSpace(path)) info["path"] = path;
        if (ports != null)
        {
            var list = ports.Distinct().OrderBy(x => x).ToArray();
            if (list.Length > 0) info["ports"] = string.Join(",", list);
        }
        return JsonSerializer.Serialize(info);
    }

    /// <summary>从进程参数里取第一个绝对路径（可执行文件/配置文件），作为"应用路径"的推断值。</summary>
    private static string? ExtractPathFromArgs(string args)
    {
        foreach (Match m in Regex.Matches(args, @"(?<p>/[^\s'""()\]]+)"))
        {
            var p = m.Groups["p"].Value;
            if (p.Length > 1) return p;
        }
        return null;
    }

    // 常见服务的默认监听端口：仅当该端口确实在监听时才采用（非 root 拿不到进程名时的兜底）
    private static readonly Dictionary<string, int[]> DefaultServicePorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nginx"] = new[] { 80, 443, 8080, 8443 },
        ["httpd"] = new[] { 80, 443 },
        ["apache2"] = new[] { 80, 443 },
        ["mysql"] = new[] { 3306 },
        ["mariadb"] = new[] { 3306 },
        ["redis"] = new[] { 6379 },
        ["postgres"] = new[] { 5432 },
        ["mongod"] = new[] { 27017 },
        ["memcached"] = new[] { 11211 },
        ["haproxy"] = new[] { 80, 443 },
        ["varnish"] = new[] { 80, 6081 },
        ["php-fpm"] = new[] { 9000 },
        ["rabbitmq"] = new[] { 5672, 15672 },
        ["gunicorn"] = new[] { 8000, 8080 },
        ["uwsgi"] = new[] { 8000, 3031 },
        ["named"] = new[] { 53 },
        ["supervisord"] = new[] { 9001 },
    };

    /// <summary>解析 <c>ss -Hltnp</c>：得到 端口→pid/进程名 映射与全部监听端口。</summary>
    private static void ParseListeningPorts(string output,
        Dictionary<string, HashSet<int>> byPid,
        Dictionary<string, HashSet<int>> byComm,
        HashSet<int> all)
    {
        foreach (var line in output.Split('\n'))
        {
            var s = line.Trim();
            if (s.Length == 0) continue;
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !parts[0].Equals("LISTEN", StringComparison.OrdinalIgnoreCase)) continue;

            var local = parts[3];
            var idx = local.LastIndexOf(':');
            if (idx <= 0 || !int.TryParse(local[(idx + 1)..], out var port)) continue;
            all.Add(port);

            foreach (Match m in Regex.Matches(s, @"\(""?(?<comm>[^"",]+?)""?,\s*pid=(?<pid>\d+)"))
            {
                var comm = m.Groups["comm"].Value;
                var pid = m.Groups["pid"].Value;
                if (!byPid.TryGetValue(pid, out var sp)) byPid[pid] = sp = new HashSet<int>();
                sp.Add(port);
                if (!byComm.TryGetValue(comm, out var sc)) byComm[comm] = sc = new HashSet<int>();
                sc.Add(port);
            }
        }
    }

    private static HashSet<int> ResolveServicePorts(string pid, string comm, string service, string args,
        Dictionary<string, HashSet<int>> byPid, Dictionary<string, HashSet<int>> byComm, HashSet<int> allListen)
    {
        var ports = new HashSet<int>();
        if (byPid.TryGetValue(pid, out var p)) ports.UnionWith(p);
        if (byComm.TryGetValue(comm, out var c)) ports.UnionWith(c);
        if (ports.Count == 0 && byComm.TryGetValue(service, out var n)) ports.UnionWith(n);

        if (ports.Count == 0)
        {
            // 兜底：从进程参数里取端口（如 redis-server 127.0.0.1:6379 / mysqld --port=3306）
            var m = Regex.Match(args, @"--port[= ](?<p>\d{2,5})");
            if (!m.Success) m = Regex.Match(args, @"[:](?<p>\d{2,5})\b");
            if (m.Success && int.TryParse(m.Groups["p"].Value, out var ap)) ports.Add(ap);
        }

        // 最终兜底：非 root 拿不到进程名时，用"该服务常见端口 ∩ 实际监听端口"
        if (ports.Count == 0 && allListen.Count > 0 && DefaultServicePorts.TryGetValue(service, out var defaults))
            ports.UnionWith(defaults.Where(allListen.Contains));

        return ports;
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
        if (dir is "config" or "conf" or "properties" or "conf.d" or "sites-enabled" or "sites-available" or "nginx")
        {
            // 这些目录下的文件名往往就是应用/站点名（nginx: sites-enabled/foo.conf）
            var file = Path.GetFileNameWithoutExtension(filePath);
            if (!string.IsNullOrEmpty(file) && file is not ("application" or "bootstrap" or "logback"))
                return file;
            if (segments.Length >= 3)
                dir = segments[^3];
        }

        return dir is "." or ".." ? null : dir;
    }

    private static string SanitizeId(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9\-_.]", "-");

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
