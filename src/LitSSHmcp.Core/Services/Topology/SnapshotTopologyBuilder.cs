using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Topology;

/// <summary>一次快照对图谱的增量：边 + 节点附加信息 + 说明。</summary>
public sealed class SnapshotGraphContribution
{
    public List<TopologyEdge> Edges { get; } = new();
    public Dictionary<string, Dictionary<string, object?>> NodeInfo { get; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; } = new();
}

/// <summary>
/// P1：把最近一次快照里的 systemd/docker/portmap/nginx_tls 转成图谱节点与边。
/// 节点：service / container / deploy / port / domain / cert；
/// 边：workload-runsOn->ssh、workload-listensOn->port、port-hostedOn->ssh、
/// app-deployedFrom->deploy、domain-exposes->app、domain-tlsBy->cert、domain-resolvesTo->ssh。
/// 只读、纯函数，不访问数据库/远端，便于测试。
/// </summary>
public static class SnapshotTopologyBuilder
{
    private const int MaxPorts = 200;
    private const int MaxDomains = 200;
    private const int MaxServices = 200;
    private const int MaxContainers = 200;

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "sshd", "systemd", "systemd-resolved", "systemd-logind", "systemd-journald",
        "dbus-daemon", "chronyd", "crond", "rsyslogd", "rpcbind", "polkitd",
        "avahi-daemon", "cupsd", "gdm", "login", "agetty", "postfix", "master",
        "dnsmasq", "dhclient", "NetworkManager", "wpa_supplicant", "auditd",
        "irqbalance", "tuned", "firewalld", "ufw", "fail2ban-server", "snapd",
        "containerd", "dockerd", "kubelet", "kube-proxy", "etcd"
    };

    private static readonly HashSet<string> ProxyProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "nginx", "haproxy", "caddy", "envoy", "traefik", "httpd", "apache2", "varnish"
    };

    private static readonly HashSet<string> AppProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "java", "node", "nodejs", "python", "python3", "php-fpm", "php", "tomcat",
        "dotnet", "gunicorn", "uwsgi", "consul"
    };

    public static SnapshotGraphContribution Build(AppConfig config, SshServerConfig server, ServerSnapshot snapshot)
    {
        var contribution = new SnapshotGraphContribution();
        if (string.IsNullOrWhiteSpace(snapshot.DataJson))
            return contribution;

        var verifiedAt = ParseTime(snapshot.CompletedAt) ?? ParseTime(snapshot.CreatedAt) ?? DateTime.UtcNow;
        var sshNode = AssetNode.Ssh(server.Id);

        try
        {
            using var doc = JsonDocument.Parse(snapshot.DataJson);
            if (!doc.RootElement.TryGetProperty("sections", out var sections) ||
                sections.ValueKind != JsonValueKind.Object)
                return contribution;

            if (sections.TryGetProperty("systemd", out var systemd))
                AddServices(server, verifiedAt, sshNode, systemd, contribution);

            if (sections.TryGetProperty("docker", out var docker))
                AddContainers(config, server, verifiedAt, sshNode, docker, contribution);

            if (sections.TryGetProperty("portmap", out var portmap))
                AddPorts(config, server, verifiedAt, sshNode, portmap, contribution);

            if (sections.TryGetProperty("nginx_tls", out var nginx))
                AddDomains(config, server, verifiedAt, sshNode, nginx, contribution);
        }
        catch (JsonException ex)
        {
            contribution.Notes.Add($"{server.Name}: 快照 JSON 解析失败，跳过域名/端口/证书入库: {ex.Message}");
        }

        return contribution;
    }

    private static void AddServices(
        SshServerConfig server,
        DateTime verifiedAt,
        string sshNode,
        JsonElement section,
        SnapshotGraphContribution contribution)
    {
        if (!TryGetData(section, out var data) ||
            !data.TryGetProperty("units", out var units) ||
            units.ValueKind != JsonValueKind.Array)
            return;

        var count = 0;
        foreach (var row in units.EnumerateArray())
        {
            if (count++ >= MaxServices)
            {
                contribution.Notes.Add($"{server.Name}: systemd 单元超过 {MaxServices} 个，图谱仅保留前 {MaxServices} 个");
                break;
            }
            if (row.ValueKind != JsonValueKind.Object)
                continue;

            var unit = String(row, "unit");
            if (string.IsNullOrWhiteSpace(unit))
                continue;

            var nodeId = ServiceNodeId(server, unit);
            var active = String(row, "active") ?? string.Empty;
            contribution.NodeInfo[nodeId] = Info(verifiedAt, unit, new Dictionary<string, object?>
            {
                ["unit"] = unit,
                ["load"] = String(row, "load"),
                ["active"] = active,
                ["sub"] = String(row, "sub"),
                ["unitDescription"] = String(row, "description"),
                ["serverId"] = server.Id,
                ["description"] = $"systemd 服务 · {server.Name} · {active}"
            });

            contribution.Edges.Add(Edge(nodeId, sshNode, "runsOn",
                $"snapshot systemd: unit={unit} active={active}", verifiedAt));
        }
    }

    private static void AddContainers(
        AppConfig config,
        SshServerConfig server,
        DateTime verifiedAt,
        string sshNode,
        JsonElement section,
        SnapshotGraphContribution contribution)
    {
        if (!TryGetData(section, out var data) ||
            !data.TryGetProperty("containers", out var containers) ||
            containers.ValueKind != JsonValueKind.Array)
            return;

        var deployEdges = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var row in containers.EnumerateArray())
        {
            if (count++ >= MaxContainers)
            {
                contribution.Notes.Add($"{server.Name}: Docker 容器超过 {MaxContainers} 个，图谱仅保留前 {MaxContainers} 个");
                break;
            }
            if (row.ValueKind != JsonValueKind.Object)
                continue;

            var name = String(row, "name");
            if (string.IsNullOrWhiteSpace(name))
                name = String(row, "id");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var containerId = ContainerNodeId(server, name);
            var image = String(row, "image") ?? string.Empty;
            var state = String(row, "state") ?? string.Empty;
            contribution.NodeInfo[containerId] = Info(verifiedAt, name, new Dictionary<string, object?>
            {
                ["containerName"] = name,
                ["containerId"] = String(row, "id"),
                ["image"] = image,
                ["state"] = state,
                ["status"] = String(row, "status"),
                ["ports"] = String(row, "ports"),
                ["createdAt"] = String(row, "createdAt"),
                ["runningFor"] = String(row, "runningFor"),
                ["serverId"] = server.Id,
                ["description"] = $"Docker 容器 · {server.Name} · {state}"
            });

            contribution.Edges.Add(Edge(containerId, sshNode, "runsOn",
                $"snapshot docker: container={name} image={image} state={state}", verifiedAt));

            foreach (var (port, proto) in PublishedPorts(String(row, "ports")))
            {
                var portId = $"port:{SanitizeId(server.Id)}:{proto}-{port}";
                if (!contribution.NodeInfo.ContainsKey(portId))
                {
                    contribution.NodeInfo[portId] = Info(verifiedAt, $"{proto}/{port} ({name})",
                        new Dictionary<string, object?>
                        {
                            ["port"] = port,
                            ["protocol"] = proto,
                            ["process"] = name,
                            ["serverId"] = server.Id,
                            ["description"] = $"容器发布端口 · {server.Name}"
                        });
                }
                contribution.Edges.Add(Edge(containerId, portId, "listensOn",
                    $"snapshot docker: container={name} published={port}/{proto}", verifiedAt));
            }

            var app = MatchContainerApplication(config, server, name);
            if (app is null)
                continue;

            var version = ImageVersion(image);
            var deployId = $"deploy:{SanitizeId(app.Id)}@{SanitizeId(version)}";
            contribution.NodeInfo[deployId] = Info(verifiedAt, $"{app.Name}@{version}",
                new Dictionary<string, object?>
                {
                    ["applicationId"] = app.Id,
                    ["version"] = version,
                    ["image"] = image,
                    ["containerId"] = containerId,
                    ["containerName"] = name,
                    ["serverId"] = server.Id,
                    ["deployedAt"] = String(row, "createdAt"),
                    ["status"] = state,
                    ["description"] = $"部署记录 · {app.Name}@{version} · {server.Name}"
                });

            if (deployEdges.Add($"{AssetNode.App(app.Id)}|{deployId}"))
            {
                contribution.Edges.Add(Edge(AssetNode.App(app.Id), deployId, "deployedFrom",
                    $"snapshot docker: app={app.Id} container={name} image={image}", verifiedAt));
            }
        }
    }

    private static void AddPorts(
        AppConfig config,
        SshServerConfig server,
        DateTime verifiedAt,
        string sshNode,
        JsonElement section,
        SnapshotGraphContribution contribution)
    {
        if (!TryGetData(section, out var data) ||
            !data.TryGetProperty("listeners", out var listeners) ||
            listeners.ValueKind != JsonValueKind.Array)
            return;

        if (data.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            contribution.Notes.Add($"{server.Name}: 端口快照已截断，图谱仅包含前 {MaxPorts} 个监听端口");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var row in listeners.EnumerateArray())
        {
            if (count++ >= MaxPorts)
                break;
            if (row.ValueKind != JsonValueKind.Object)
                continue;

            var proto = String(row, "proto") ?? "tcp";
            var port = Int(row, "port");
            if (port is null or <= 0)
                continue;

            var process = String(row, "process") ?? string.Empty;
            if (IgnoredProcesses.Contains(process))
                continue;
            if (!seen.Add($"{proto}:{port}"))
                continue;

            var bind = String(row, "bind") ?? string.Empty;
            var exe = String(row, "exe") ?? string.Empty;
            var cmdline = String(row, "cmdline") ?? string.Empty;
            var pid = Long(row, "pid");
            var user = String(row, "user");
            var unit = String(row, "unit");

            var portId = $"port:{SanitizeId(server.Id)}:{proto}-{port}";
            var label = string.IsNullOrWhiteSpace(process)
                ? $"{proto}/{port}"
                : $"{proto}/{port} ({process})";

            MergeNodeInfo(contribution, portId, Info(verifiedAt, label, new Dictionary<string, object?>
            {
                ["port"] = port,
                ["protocol"] = proto,
                ["bind"] = bind,
                ["anyBind"] = Bool(row, "anyBind"),
                ["process"] = process,
                ["pid"] = pid,
                ["user"] = user,
                ["unit"] = unit,
                ["exe"] = exe,
                ["cmdline"] = cmdline,
                ["serverId"] = server.Id,
                ["description"] = $"监听 {proto}/{port} · {server.Name}"
            }));

            contribution.Edges.Add(Edge(portId, sshNode, "hostedOn",
                $"snapshot portmap: {proto}/{port} bind={bind} process={process} pid={pid}", verifiedAt));

            if (!string.IsNullOrWhiteSpace(unit))
            {
                var serviceId = ServiceNodeId(server, unit);
                if (!contribution.NodeInfo.ContainsKey(serviceId))
                {
                    contribution.NodeInfo[serviceId] = Info(verifiedAt, unit,
                        new Dictionary<string, object?>
                        {
                            ["unit"] = unit,
                            ["serverId"] = server.Id,
                            ["description"] = $"systemd 服务 · {server.Name} · 监听 {proto}/{port}"
                        });
                }
                contribution.Edges.Add(Edge(serviceId, portId, "listensOn",
                    $"snapshot portmap: unit={unit} port={port}", verifiedAt));
            }

            var app = MatchApplication(config, server, port.Value, process, exe, cmdline);
            if (app is not null)
            {
                contribution.Edges.Add(Edge(AssetNode.App(app.Id), portId, "listensOn",
                    $"snapshot portmap: process={process} port={port}", verifiedAt));
            }
            else if (IsAppProcess(process))
            {
                contribution.Edges.Add(Edge(PendingAppId(process), portId, "listensOn",
                    $"snapshot portmap(未登记应用): process={process} port={port}", verifiedAt));
            }
            else
            {
                var ds = MatchDatasource(config, server, port.Value);
                if (ds is not null)
                {
                    contribution.Edges.Add(Edge(AssetNode.Ds(ds.Id), portId, "listensOn",
                        $"snapshot portmap: data service process={process} port={port}", verifiedAt));
                }
                else
                {
                    var pendingDs = PendingDatasourceId(process);
                    if (pendingDs is not null)
                        contribution.Edges.Add(Edge(pendingDs, portId, "listensOn",
                            $"snapshot portmap(未登记数据源): process={process} port={port}", verifiedAt));
                }
            }
        }
    }

    private static void AddDomains(
        AppConfig config,
        SshServerConfig server,
        DateTime verifiedAt,
        string sshNode,
        JsonElement section,
        SnapshotGraphContribution contribution)
    {
        if (!TryGetData(section, out var data) ||
            !data.TryGetProperty("domains", out var domains) ||
            domains.ValueKind != JsonValueKind.Array)
            return;

        if (data.TryGetProperty("configTruncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            contribution.Notes.Add($"{server.Name}: nginx 配置已截断，域名/证书可能不完整");

        var proxyApp = MatchProxyApplication(config, server);
        var proxyNode = proxyApp is not null ? AssetNode.App(proxyApp.Id) : PendingAppId("nginx");

        var count = 0;
        foreach (var row in domains.EnumerateArray())
        {
            if (count++ >= MaxDomains)
            {
                contribution.Notes.Add($"{server.Name}: 域名超过 {MaxDomains} 个，图谱仅保留前 {MaxDomains} 个");
                break;
            }
            if (row.ValueKind != JsonValueKind.Object)
                continue;

            var domain = String(row, "domain");
            if (string.IsNullOrWhiteSpace(domain) || domain == "_")
                continue;

            var domainId = $"domain:{SanitizeId(domain)}";
            var ports = IntArray(row, "ports");
            var ssl = Bool(row, "ssl");

            contribution.NodeInfo[domainId] = Info(verifiedAt, domain, new Dictionary<string, object?>
            {
                ["ports"] = string.Join(",", ports),
                ["ssl"] = ssl,
                ["serverId"] = server.Id,
                ["description"] = $"nginx 站点 · {server.Name}"
            });

            contribution.Edges.Add(Edge(domainId, sshNode, "resolvesTo",
                $"snapshot nginx_tls: domain={domain} server={server.Name}", verifiedAt));
            contribution.Edges.Add(Edge(domainId, proxyNode, "exposes",
                $"snapshot nginx_tls: domain={domain}", verifiedAt));

            if (!row.TryGetProperty("certificates", out var certs) || certs.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var cert in certs.EnumerateArray())
            {
                if (cert.ValueKind != JsonValueKind.Object)
                    continue;

                var path = String(cert, "path") ?? string.Empty;
                var subjectCn = String(cert, "subjectCn");
                var notAfter = String(cert, "notAfter");
                var certId = $"cert:{ShortHash($"{server.Id}|{path}|{subjectCn}")}";
                var label = string.IsNullOrWhiteSpace(subjectCn) ? $"证书 {ShortHash(path)}" : $"证书 {subjectCn}";

                contribution.NodeInfo[certId] = Info(verifiedAt, label, new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["subjectCn"] = subjectCn,
                    ["expiresAt"] = notAfter,
                    ["daysRemaining"] = Long(cert, "daysRemaining"),
                    ["expired"] = Bool(cert, "expired"),
                    ["dnsNames"] = StringArray(cert, "dnsNames"),
                    ["serverId"] = server.Id,
                    ["description"] = $"TLS 证书 · {server.Name} · 到期 {notAfter}"
                });

                contribution.Edges.Add(Edge(domainId, certId, "tlsBy",
                    $"snapshot nginx_tls: domain={domain} cert={path} notAfter={notAfter}", verifiedAt));
            }
        }
    }

    private static ApplicationConfig? MatchApplication(
        AppConfig config, SshServerConfig server, int port, string process, string exe, string cmdline)
    {
        var candidates = config.Applications.Where(a => ApplicationOnServer(a, server)).ToArray();
        var text = $"{process} {exe} {cmdline}";
        var byName = candidates.FirstOrDefault(a => NameMatches(a, text));
        if (byName is not null)
            return byName;

        // nginx/haproxy 等代理进程不能按端口反推后端应用（80/443 往往由代理占用）
        return ProxyProcesses.Contains(process)
            ? null
            : candidates.FirstOrDefault(a => a.Port == port);
    }

    private static ApplicationConfig? MatchProxyApplication(AppConfig config, SshServerConfig server) =>
        config.Applications.FirstOrDefault(a =>
            ApplicationOnServer(a, server) &&
            (a.Type.Contains("nginx", StringComparison.OrdinalIgnoreCase) ||
             a.Name.Contains("nginx", StringComparison.OrdinalIgnoreCase)));

    private static DataSourceConfig? MatchDatasource(AppConfig config, SshServerConfig server, int port) =>
        config.DataSources.FirstOrDefault(d =>
            d.Port == port && DatasourceOnServer(d, server) && DataServiceType(d.Type) is not null);

    private static bool ApplicationOnServer(ApplicationConfig app, SshServerConfig server) =>
        string.IsNullOrWhiteSpace(app.Host) ||
        app.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase) ||
        app.Host.Equals(server.Name, StringComparison.OrdinalIgnoreCase);

    private static bool DatasourceOnServer(DataSourceConfig ds, SshServerConfig server)
    {
        if (IsLocalHost(ds.Host))
            return ds.AccessMode == AccessMode.SshTunnel &&
                   string.Equals(ds.TunnelServerId, server.Id, StringComparison.OrdinalIgnoreCase);
        return ds.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase) ||
               ds.Host.Equals(server.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalHost(string? host)
    {
        var h = (host ?? string.Empty).Trim();
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h is "127.0.0.1" or "::1";
    }

    private static bool NameMatches(ApplicationConfig app, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (app.Name.Length >= 2 && text.Contains(app.Name, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(app.ContainerName) &&
            text.Contains(app.ContainerName, StringComparison.OrdinalIgnoreCase))
            return true;
        return app.Id.Length >= 4 && text.Contains(app.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAppProcess(string process) =>
        ProxyProcesses.Contains(process) || AppProcesses.Contains(process);

    private static string? DataServiceType(string? type) => (type ?? string.Empty).ToLowerInvariant() switch
    {
        "mysql" or "mariadb" => "mysql",
        "redis" => "redis",
        "postgres" or "postgresql" => "postgres",
        _ => null
    };

    private static string? PendingDatasourceId(string process)
    {
        var type = process.ToLowerInvariant() switch
        {
            "mysqld" or "mysqld_safe" => "mysql",
            "redis-server" or "redis" => "redis",
            "postgres" => "postgres",
            _ => null
        };
        return type is null ? null : $"ds:disc:{type}";
    }

    private static ApplicationConfig? MatchContainerApplication(
        AppConfig config, SshServerConfig server, string containerName)
    {
        var candidates = config.Applications.Where(a => ApplicationOnServer(a, server)).ToArray();
        return candidates.FirstOrDefault(a =>
                   !string.IsNullOrWhiteSpace(a.ContainerName) &&
                   a.ContainerName.Equals(containerName, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(a =>
                   a.Name.Equals(containerName, StringComparison.OrdinalIgnoreCase) ||
                   a.Id.Equals(containerName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<(int Port, string Proto)> PublishedPorts(string? value)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Trim();
            var arrow = segment.IndexOf("->", StringComparison.Ordinal);
            if (arrow <= 0)
                continue;

            var host = segment[..arrow];
            var container = segment[(arrow + 2)..];
            var slash = container.IndexOf('/');
            var proto = slash >= 0 ? container[(slash + 1)..].Trim().ToLowerInvariant() : "tcp";
            if (proto.Length == 0)
                proto = "tcp";

            var colon = host.LastIndexOf(':');
            var portText = colon >= 0 ? host[(colon + 1)..] : host;
            if (!int.TryParse(portText, out var port) || port <= 0)
                continue;
            if (seen.Add($"{proto}:{port}"))
                yield return (port, proto);
        }
    }

    private static string ImageVersion(string image)
    {
        var value = (image ?? string.Empty).Trim();
        if (value.Length == 0)
            return "unknown";

        var digest = value.IndexOf('@');
        if (digest >= 0 && digest < value.Length - 1)
            return value[(digest + 1)..];

        var slash = value.LastIndexOf('/');
        var colon = value.LastIndexOf(':');
        return colon > slash ? value[(colon + 1)..] : "latest";
    }

    private static string ServiceNodeId(SshServerConfig server, string unit)
    {
        var name = unit.Trim();
        if (name.EndsWith(".service", StringComparison.OrdinalIgnoreCase))
            name = name[..^".service".Length];
        return $"service:{SanitizeId(server.Id)}:{SanitizeId(name)}";
    }

    private static string ContainerNodeId(SshServerConfig server, string name) =>
        $"container:{SanitizeId(server.Id)}:{SanitizeId(name)}";

    private static void MergeNodeInfo(
        SnapshotGraphContribution contribution, string nodeId, Dictionary<string, object?> info)
    {
        if (!contribution.NodeInfo.TryGetValue(nodeId, out var existing))
        {
            contribution.NodeInfo[nodeId] = info;
            return;
        }

        foreach (var (key, value) in info)
            existing[key] = value;
    }

    private static bool TryGetData(JsonElement section, out JsonElement data)
    {
        data = default;
        if (section.ValueKind != JsonValueKind.Object)
            return false;
        if (section.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String &&
            status.GetString() is "failed" or "skipped")
            return false;
        return section.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;
    }

    private static TopologyEdge Edge(string from, string to, string type, string evidence, DateTime at) => new()
    {
        From = from,
        To = to,
        Type = type,
        Evidence = evidence,
        Source = "discovered",
        FirstSeenAt = at,
        LastSeenAt = at,
        Confidence = 0.9,
        ValidFrom = at,
        LastVerifiedAt = at
    };

    private static Dictionary<string, object?> Info(
        DateTime verifiedAt, string label, Dictionary<string, object?> info)
    {
        info["label"] = label;
        info["source"] = "snapshot";
        info["confidence"] = "high";
        info["disputed"] = false;
        info["lastVerifiedAt"] = verifiedAt.ToString("O");
        return info;
    }

    private static DateTime? ParseTime(string? value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static long? Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;

    private static bool Bool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed));

    private static int[] IntArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<int>();
        return value.EnumerateArray()
            .Where(x => x.TryGetInt32(out _))
            .Select(x => x.GetInt32())
            .ToArray();
    }

    private static string[] StringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToArray();
    }

    private static string PendingAppId(string name) => $"app:disc:{SanitizeId(name)}";

    private static string SanitizeId(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9\-_.]", "-");

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
}
