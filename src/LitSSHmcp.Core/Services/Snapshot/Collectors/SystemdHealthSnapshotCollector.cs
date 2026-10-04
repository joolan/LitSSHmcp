using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Snapshot.Collectors;

/// <summary>
/// systemd 单元健康聚合：全量 service 单元的 LOAD/ACTIVE/SUB 计数、失败单元清单与 is-system-running 总态。
/// 非 systemd 系统（无 systemctl / 无输出）→ skipped，不算快照失败。
/// </summary>
public sealed class SystemdHealthSnapshotCollector : ISnapshotCollector
{
    public string Name => "systemd";
    public int Order => 40;

    private const string ProbeCommand =
        "systemctl list-units --type=service --all --no-legend --no-pager 2>/dev/null | head -400; " +
        "echo '##state'; systemctl is-system-running 2>/dev/null";

    public async Task<CollectorResult> CollectAsync(SnapshotContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await context.ExecuteAsync(ProbeCommand, false, ct);

        // 传输层错误优先（认证/断网等整体失败）
        if (!result.Success && result.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(result) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var sections = ResourceSnapshotCollector.SplitSections(result.Output);
        var unitLines = sections.GetValueOrDefault(string.Empty) ?? string.Empty;
        var state = sections.GetValueOrDefault("state")?.Trim();

        if (string.IsNullOrWhiteSpace(unitLines) && string.IsNullOrWhiteSpace(state))
            return CollectorResult.Skip("systemctl 不可用(非 systemd 系统或命令失败)") with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var units = ParseUnits(unitLines);
        var counts = new Dictionary<string, long>();
        var failed = new List<Dictionary<string, object?>>();
        foreach (var unit in units)
        {
            counts[unit.Active] = counts.GetValueOrDefault(unit.Active) + 1;
            if (unit.Active.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                failed.Add(new Dictionary<string, object?>
                {
                    ["unit"] = unit.Name,
                    ["sub"] = unit.Sub,
                    ["description"] = unit.Description
                });
            }
        }

        var data = new Dictionary<string, object?>
        {
            ["systemState"] = string.IsNullOrWhiteSpace(state) ? null : state,
            ["total"] = units.Count,
            ["counts"] = counts,
            ["failed"] = failed
        };

        sw.Stop();
        return CollectorResult.Ok(data) with { DurationMs = sw.Elapsed.TotalMilliseconds };
    }

    public sealed record UnitInfo(string Name, string Load, string Active, string Sub, string Description);

    /// <summary>
    /// 解析 systemctl list-units 行: <c>nginx.service loaded active running A high performance web server</c>。
    /// failed 单元行首的 <c>●</c> 标记（非 --plain 时也会出现）先剥掉。
    /// </summary>
    public static List<UnitInfo> ParseUnits(string output)
    {
        var units = new List<UnitInfo>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().TrimStart('●', ' ', '\t').Trim();
            if (line.Length == 0)
                continue;

            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4)
                continue;
            if (!tokens[0].EndsWith(".service", StringComparison.Ordinal))
                continue;

            units.Add(new UnitInfo(
                tokens[0],
                tokens[1],
                tokens[2],
                tokens[3],
                tokens.Length > 4 ? string.Join(' ', tokens[4..]) : string.Empty));
        }
        return units;
    }
}
