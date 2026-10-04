using System.Text.Json;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Snapshot.Collectors;

/// <summary>
/// Docker 容器快照：守护进程概览（版本/容器与镜像计数/存储驱动/CPU/内存）、
/// 容器清单（名称/镜像/状态/端口/创建时间）与运行中容器的资源占用（CPU/内存/网络/块IO/PIDs）。
/// 未安装 docker → skipped；已安装但守护进程不可用 → available=true/daemonRunning=false（降级）。
/// 全部命令用 --format '{{json .}}' 输出 JSON，按行 + JSON 键解析（无标记，PTY 回显安全）。
/// </summary>
public sealed class DockerSnapshotCollector : ISnapshotCollector
{
    public string Name => "docker";
    public int Order => 25;

    private const string DetectCommand = "command -v docker 2>/dev/null";
    private const string InfoCommand = "docker info --format '{{json .}}' 2>/dev/null | head -c 30000";
    private const string PsStatsCommand =
        "docker ps -a --format '{{json .}}' 2>/dev/null | head -300; " +
        "docker stats --no-stream --format '{{json .}}' 2>/dev/null | head -300";

    public async Task<CollectorResult> CollectAsync(SnapshotContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var detect = await context.ExecuteAsync(DetectCommand, context.Elevate, ct);
        if (!detect.Success && detect.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(detect) with { DurationMs = sw.Elapsed.TotalMilliseconds };
        if (string.IsNullOrWhiteSpace(detect.Output))
            return CollectorResult.Skip("docker 未安装或不在 PATH") with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var infoResult = await context.ExecuteAsync(InfoCommand, context.Elevate, ct);
        if (!infoResult.Success && infoResult.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(infoResult) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var psResult = await context.ExecuteAsync(PsStatsCommand, context.Elevate, ct);
        if (!psResult.Success && psResult.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(psResult) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var info = ParseDockerInfo(infoResult.Output);
        var (containers, stats) = ParsePsAndStats(psResult.Output);

        if (info is null && containers.Count == 0)
        {
            sw.Stop();
            return CollectorResult.Ok(
                new Dictionary<string, object?> { ["available"] = true, ["daemonRunning"] = false },
                degraded: true,
                note: "docker 已安装但守护进程不可用(daemon 未运行或无权限)") with { DurationMs = sw.Elapsed.TotalMilliseconds };
        }

        var byState = new Dictionary<string, int>();
        foreach (var container in containers)
        {
            var state = container.GetValueOrDefault("state") as string ?? "unknown";
            byState[state] = byState.GetValueOrDefault(state) + 1;
        }

        var data = new Dictionary<string, object?>
        {
            ["available"] = true,
            ["daemonRunning"] = true,
            ["serverVersion"] = info?.GetValueOrDefault("serverVersion"),
            ["info"] = info,
            ["containers"] = containers,
            ["stats"] = stats,
            ["counts"] = new Dictionary<string, object?>
            {
                ["total"] = containers.Count,
                ["byState"] = byState
            }
        };

        sw.Stop();
        return CollectorResult.Ok(data) with { DurationMs = sw.Elapsed.TotalMilliseconds };
    }

    /// <summary>解析 `docker info --format '{{json .}}'`（取首个 JSON 行，提取常用字段）。失败返回 null。</summary>
    public static Dictionary<string, object?>? ParseDockerInfo(string output)
    {
        var line = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith('{'));
        if (line is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            return new Dictionary<string, object?>
            {
                ["serverVersion"] = Str(e, "ServerVersion"),
                ["operatingSystem"] = Str(e, "OperatingSystem"),
                ["driver"] = Str(e, "Driver"),
                ["hostname"] = Str(e, "Name"),
                ["containers"] = Long(e, "Containers"),
                ["containersRunning"] = Long(e, "ContainersRunning"),
                ["containersPaused"] = Long(e, "ContainersPaused"),
                ["containersStopped"] = Long(e, "ContainersStopped"),
                ["images"] = Long(e, "Images"),
                ["cpus"] = Long(e, "NCPU"),
                ["memoryBytes"] = Long(e, "MemTotal")
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 `docker ps -a` 与 `docker stats --no-stream` 的合并输出（每行一个 JSON）。
    /// 按 JSON 键区分：含 CPUPerc 的是 stats，含 Names/Image 的是容器。
    /// </summary>
    public static (List<Dictionary<string, object?>> Containers, List<Dictionary<string, object?>> Stats) ParsePsAndStats(string output)
    {
        var containers = new List<Dictionary<string, object?>>();
        var stats = new List<Dictionary<string, object?>>();

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('{'))
                continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;   // PTY 回显碎片等非 JSON 行
            }

            using (doc)
            {
                var e = doc.RootElement;
                if (e.TryGetProperty("CPUPerc", out _))
                {
                    stats.Add(new Dictionary<string, object?>
                    {
                        ["name"] = Str(e, "Name"),
                        ["id"] = Str(e, "ID"),
                        ["cpuPercent"] = Str(e, "CPUPerc"),
                        ["memUsage"] = Str(e, "MemUsage"),
                        ["memPercent"] = Str(e, "MemPerc"),
                        ["netIO"] = Str(e, "NetIO"),
                        ["blockIO"] = Str(e, "BlockIO"),
                        ["pids"] = Str(e, "PIDs")
                    });
                }
                else if (e.TryGetProperty("Names", out _) || e.TryGetProperty("Image", out _))
                {
                    containers.Add(new Dictionary<string, object?>
                    {
                        ["id"] = Str(e, "ID"),
                        ["name"] = Str(e, "Names"),
                        ["image"] = Str(e, "Image"),
                        ["state"] = Str(e, "State"),
                        ["status"] = Str(e, "Status"),
                        ["ports"] = Str(e, "Ports"),
                        ["createdAt"] = Str(e, "CreatedAt"),
                        ["runningFor"] = Str(e, "RunningFor")
                    });
                }
            }
        }

        return (containers, stats);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var parsed))
            return parsed;
        return null;
    }
}
