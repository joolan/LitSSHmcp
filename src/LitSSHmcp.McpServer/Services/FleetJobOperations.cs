using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 多机巡检 / 多机快照的 job-only operation。
/// 不新增 MCP 工具：job_start(operation=fleet_inventory|fleet_snapshot_refresh) 通过
/// JobManager 注册的请求描述符完成多目标授权，工具面保持精简。
/// </summary>
public sealed class FleetJobOperations
{
    private readonly IConfigService _config;
    private readonly ISnapshotStore _snapshotStore;
    private readonly ISnapshotService _snapshotService;
    private readonly ISshService _ssh;

    public FleetJobOperations(IConfigService config, ISnapshotStore snapshotStore,
        ISnapshotService snapshotService, ISshService ssh)
    {
        _config = config;
        _snapshotStore = snapshotStore;
        _snapshotService = snapshotService;
        _ssh = ssh;
    }

    public void Register(JobManager jobs)
    {
        jobs.Register("fleet_inventory",
            "多机巡检：在线状态 + 端口↔进程 + Docker + Jenkins（可用 refresh=true 重采快照）",
            new ToolSecurityDescriptor("fleet_inventory", ToolTargetKind.MultiServer,
                new[] { "serverIds" }, ToolAccessLevel.Read, false, false),
            PrepareInventoryAsync);

        jobs.Register("fleet_snapshot_refresh",
            "多机整机快照刷新：逐台采集并返回 snapshotId/状态/错误",
            new ToolSecurityDescriptor("fleet_snapshot_refresh", ToolTargetKind.MultiServer,
                new[] { "serverIds" }, ToolAccessLevel.Read, false, false),
            PrepareFleetSnapshotAsync);
    }

    private async Task<JobManager.JobPrepared> PrepareInventoryAsync(JsonElement arguments, CancellationToken ct)
    {
        var config = await _config.LoadConfigAsync();
        var (servers, fail) = ResolveServers(config, ReadString(arguments, "serverIds"));
        if (fail is { } f)
            return JobManager.JobPrepared.Fail(f.Status, f.Error);

        var refresh = ReadBool(arguments, "refresh");
        var maxAgeMinutes = Math.Clamp(ReadInt(arguments, "maxAgeMinutes") ?? 60, 1, 24 * 60);
        var options = config.Snapshot ?? new SnapshotConfig();

        return JobManager.JobPrepared.Ready(async (ctx, ct2) =>
        {
            var progress = ctx.AsProgress<FleetProgress>(p => (
                p.Done, p.Total,
                JsonSerializer.Serialize(new
                {
                    server = p.Server,
                    phase = p.Phase,
                    done = p.Done,
                    total = p.Total,
                    status = p.Status,
                    note = p.Note
                }, JsonOutput.Options)),
                p => p.Phase == "start" ? JobEventKinds.Phase : JobEventKinds.Progress);

            var dto = await InventoryAsync(servers, options, refresh, maxAgeMinutes, progress, ct2);
            return JobManager.JobOutcome.Succeed(dto);
        });
    }

    private async Task<JobManager.JobPrepared> PrepareFleetSnapshotAsync(JsonElement arguments, CancellationToken ct)
    {
        var config = await _config.LoadConfigAsync();
        var (servers, fail) = ResolveServers(config, ReadString(arguments, "serverIds"));
        if (fail is { } f)
            return JobManager.JobPrepared.Fail(f.Status, f.Error);

        var force = ReadBool(arguments, "force");
        var collectSections = SnapshotRequest.ParseSections(ReadString(arguments, "profile"), ReadString(arguments, "sections"));
        var options = config.Snapshot ?? new SnapshotConfig();

        return JobManager.JobPrepared.Ready(async (ctx, ct2) =>
        {
            var progress = ctx.AsProgress<FleetProgress>(p => (
                p.Done, p.Total,
                JsonSerializer.Serialize(new
                {
                    server = p.Server,
                    phase = p.Phase,
                    done = p.Done,
                    total = p.Total,
                    status = p.Status,
                    note = p.Note
                }, JsonOutput.Options)),
                p => p.Phase == "start" ? JobEventKinds.Phase : JobEventKinds.Progress);

            var dto = await RefreshFleetAsync(servers, options, collectSections, force, progress, ct2);
            return JobManager.JobOutcome.Succeed(dto);
        });
    }

    private async Task<FleetInventoryDto> InventoryAsync(IReadOnlyList<SshServerConfig> servers,
        SnapshotConfig options, bool refresh, int maxAgeMinutes, IProgress<FleetProgress>? progress,
        CancellationToken ct)
    {
        var dto = new FleetInventoryDto { Success = true, Status = "ok" };
        var done = 0;
        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var probe = await _ssh.ProbeConnectionAsync(server, ct);
                var latest = await _snapshotStore.GetLatestAsync(server.Id, ct);
                var stale = IsStale(latest, maxAgeMinutes);
                if (refresh || latest is null || stale)
                {
                    var refreshed = await _snapshotService.RefreshAsync(server, options,
                        new[] { "portmap", "docker", "systemd" }, force: true, ct);
                    latest = refreshed.Snapshot ?? latest;
                }

                dto.Servers.Add(BuildInventory(server, probe, latest, stale));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                dto.Servers.Add(new FleetInventoryServerDto
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Host = server.Host,
                    Error = ex.Message
                });
            }
            done++;
            var item = dto.Servers[^1];
            progress?.Report(new FleetProgress(done, servers.Count, server.Name, "done",
                item.Online ? "online" : "offline", item.Error));
        }

        dto.Success = dto.Servers.Count > 0 && dto.Servers.All(s => s.Error is null);
        dto.Status = dto.Success ? "ok" : "partial";
        return dto;
    }

    private static FleetInventoryServerDto BuildInventory(SshServerConfig server,
        ConnectionProbeResult probe, ServerSnapshot? snapshot, bool stale)
    {
        var item = new FleetInventoryServerDto
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Host = server.Host,
            Online = probe.Success,
            OnlineStatus = probe.Success ? "online" : probe.ErrorKind,
            OnlineError = probe.Success ? null : probe.Error,
            SnapshotId = snapshot?.Id,
            SnapshotAt = snapshot?.CreatedAt,
            SnapshotStatus = snapshot?.Status.ToString().ToLowerInvariant(),
            SnapshotStale = stale
        };

        if (snapshot?.DataJson is not { Length: > 0 } json)
            return item;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("sections", out var sections) ||
                sections.ValueKind != JsonValueKind.Object)
                return item;

            if (sections.TryGetProperty("portmap", out var portmap) &&
                portmap.TryGetProperty("data", out var portData) &&
                portData.TryGetProperty("listeners", out var listeners) &&
                listeners.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in listeners.EnumerateArray().Take(200))
                {
                    item.Ports.Add(new FleetPortDto
                    {
                        Proto = ReadString(row, "proto"),
                        Bind = ReadString(row, "bind"),
                        Port = ReadInt(row, "port"),
                        Process = ReadString(row, "process"),
                        Pid = ReadInt(row, "pid"),
                        User = ReadString(row, "user"),
                        Unit = ReadString(row, "unit"),
                        Exe = ReadString(row, "exe")
                    });
                }
            }

            if (sections.TryGetProperty("docker", out var docker) &&
                docker.TryGetProperty("data", out var dockerData))
            {
                item.Docker = new FleetDockerDto
                {
                    Installed = ReadBool(dockerData, "available"),
                    Active = ReadBool(dockerData, "daemonRunning"),
                    Version = ReadString(dockerData, "serverVersion"),
                    ContainersTotal = ReadInt(dockerData, "counts", "total") ?? 0,
                    ContainersRunning = ReadInt(dockerData, "counts", "byState", "running") ?? 0
                };
            }

            if (sections.TryGetProperty("systemd", out var systemd) &&
                systemd.TryGetProperty("data", out var systemdData) &&
                systemdData.TryGetProperty("units", out var units) &&
                units.ValueKind == JsonValueKind.Array)
            {
                foreach (var unit in units.EnumerateArray())
                {
                    var name = ReadString(unit, "unit");
                    if (name is null || !name.Contains("jenkins", StringComparison.OrdinalIgnoreCase))
                        continue;
                    item.Jenkins = new FleetJenkinsDto
                    {
                        Installed = true,
                        Active = string.Equals(ReadString(unit, "active"), "active",
                            StringComparison.OrdinalIgnoreCase),
                        Unit = name
                    };
                    break;
                }
            }

            if (item.Jenkins is null || !item.Jenkins.Installed)
            {
                var jenkinsPort = item.Ports.FirstOrDefault(p =>
                    (p.Process?.Contains("jenkins", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (p.Unit?.Contains("jenkins", StringComparison.OrdinalIgnoreCase) ?? false));
                if (jenkinsPort is not null)
                {
                    item.Jenkins = new FleetJenkinsDto
                    {
                        Installed = true,
                        Active = true,
                        Unit = jenkinsPort.Unit,
                        Port = jenkinsPort.Port
                    };
                }
            }
            else
            {
                var port = item.Ports.FirstOrDefault(p =>
                    (p.Unit?.Contains("jenkins", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (p.Process?.Contains("jenkins", StringComparison.OrdinalIgnoreCase) ?? false));
                if (port is not null && item.Jenkins is not null)
                    item.Jenkins.Port = port.Port;
            }
        }
        catch (JsonException)
        {
            item.Error = "快照数据不是合法 JSON";
        }

        return item;
    }

    private async Task<FleetSnapshotRefreshDto> RefreshFleetAsync(IReadOnlyList<SshServerConfig> servers,
        SnapshotConfig options, IReadOnlyCollection<string>? collectSections, bool force,
        IProgress<FleetProgress>? progress, CancellationToken ct)
    {
        var dto = new FleetSnapshotRefreshDto { Success = true, Status = "ok" };
        var done = 0;
        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await _snapshotService.RefreshAsync(server, options, collectSections, force, ct);
                dto.Results.Add(new FleetSnapshotItemDto
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Host = server.Host,
                    Status = result.Status,
                    SnapshotId = result.SnapshotId,
                    Error = result.Error,
                    DurationMs = result.Snapshot?.DurationMs
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                dto.Results.Add(new FleetSnapshotItemDto
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Host = server.Host,
                    Status = "failed",
                    Error = ex.Message
                });
            }
            done++;
            var item = dto.Results[^1];
            progress?.Report(new FleetProgress(done, servers.Count, server.Name, "done",
                item.Status, item.Error));
        }

        dto.Success = dto.Results.Count > 0 && dto.Results.All(r => r.Status is "succeeded" or "fresh");
        dto.Status = dto.Success ? "ok" : "partial";
        return dto;
    }

    private static (List<SshServerConfig> Servers, (string Status, string Error)? Fail) ResolveServers(
        AppConfig config, string? serverIds)
    {
        var references = Split(serverIds);
        var servers = new List<SshServerConfig>();
        if (references is null)
        {
            servers.AddRange(config.Servers.Where(s => !s.Disabled));
        }
        else
        {
            foreach (var reference in references)
            {
                var (server, status, error) = ToolSupport.ResolveServer(config, reference);
                if (server is null)
                    return (servers, (status ?? "server_not_found", error ?? $"服务器未找到: {reference}"));
                if (servers.All(s => !string.Equals(s.Id, server.Id, StringComparison.Ordinal)))
                    servers.Add(server);
            }
        }

        if (servers.Count == 0)
            return (servers, ("no_servers", "没有可用的服务器目标"));
        if (servers.Count > 100)
            return (servers, ("too_many_targets", "单次最多 100 台服务器"));
        return (servers, null);
    }

    private static string[]? Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ? v[6..] :
                         v.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase) ? v[4..] : v)
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .ToArray();
        return parts.Length == 0 ? null : parts;
    }

    private static bool IsStale(ServerSnapshot? snapshot, int maxAgeMinutes)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.CreatedAt))
            return true;
        return !DateTimeOffset.TryParse(snapshot.CreatedAt, out var createdAt) ||
               DateTimeOffset.UtcNow - createdAt > TimeSpan.FromMinutes(maxAgeMinutes);
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (!element.TryGetProperty(name, out element))
                return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static int? ReadInt(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (!element.TryGetProperty(name, out element))
                return null;
        }
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
            return value;
        return null;
    }

    private static bool ReadBool(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (!element.TryGetProperty(name, out element))
                return false;
        }
        return element.ValueKind == JsonValueKind.True;
    }
}

public sealed record FleetProgress(int Done, int Total, string Server, string Phase,
    string? Status = null, string? Note = null);

public sealed class FleetInventoryDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public List<FleetInventoryServerDto> Servers { get; set; } = new();
}

public sealed class FleetInventoryServerDto
{
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Host { get; set; } = "";
    public bool Online { get; set; }
    public string? OnlineStatus { get; set; }
    public string? OnlineError { get; set; }
    public long? SnapshotId { get; set; }
    public string? SnapshotAt { get; set; }
    public string? SnapshotStatus { get; set; }
    public bool SnapshotStale { get; set; }
    public List<FleetPortDto> Ports { get; set; } = new();
    public FleetDockerDto? Docker { get; set; }
    public FleetJenkinsDto? Jenkins { get; set; }
    public string? Error { get; set; }
}

public sealed class FleetPortDto
{
    public string? Proto { get; set; }
    public string? Bind { get; set; }
    public int? Port { get; set; }
    public string? Process { get; set; }
    public int? Pid { get; set; }
    public string? User { get; set; }
    public string? Unit { get; set; }
    public string? Exe { get; set; }
}

public sealed class FleetDockerDto
{
    public bool Installed { get; set; }
    public bool Active { get; set; }
    public string? Version { get; set; }
    public int ContainersTotal { get; set; }
    public int ContainersRunning { get; set; }
}

public sealed class FleetJenkinsDto
{
    public bool Installed { get; set; }
    public bool Active { get; set; }
    public string? Unit { get; set; }
    public int? Port { get; set; }
}

public sealed class FleetSnapshotRefreshDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public List<FleetSnapshotItemDto> Results { get; set; } = new();
}

public sealed class FleetSnapshotItemDto
{
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Host { get; set; } = "";
    public string Status { get; set; } = "";
    public long? SnapshotId { get; set; }
    public string? Error { get; set; }
    public double? DurationMs { get; set; }
}
