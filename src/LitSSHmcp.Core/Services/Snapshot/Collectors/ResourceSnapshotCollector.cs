using System.Globalization;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Snapshot.Collectors;

/// <summary>
/// 服务器态势快照：CPU/内存/磁盘/负载/系统信息。
/// 单条组合命令一次往返（echo '##key' 分段，远端与解析端都不依赖 bash 专有语法）。
/// </summary>
public sealed class ResourceSnapshotCollector : ISnapshotCollector
{
    public string Name => "resource";
    public int Order => 10;

    // free/df 缺失的极简系统回退 /proc；各段均可独立缺失，解析端容忍。
    private const string ProbeCommand =
        "echo '##loadavg'; cat /proc/loadavg 2>/dev/null; " +
        "echo '##mem'; free -m 2>/dev/null || cat /proc/meminfo 2>/dev/null; " +
        "echo '##disk'; df -hP -x tmpfs -x devtmpfs -x overlay -x squashfs 2>/dev/null || df -hP 2>/dev/null; " +
        "echo '##uptime'; cat /proc/uptime 2>/dev/null; " +
        "echo '##cpu'; grep -m1 'model name' /proc/cpuinfo 2>/dev/null; " +
        "echo '##cores'; nproc 2>/dev/null; " +
        "echo '##os'; cat /etc/os-release 2>/dev/null | head -6; " +
        "echo '##kernel'; uname -r; echo '##arch'; uname -m; " +
        "echo '##host'; hostname; echo '##ip'; hostname -I 2>/dev/null; " +
        // top 进程: 分开写 -o 以兼容 CentOS7 procps 3.3.10(逗号列首带 = 会解析异常)
        "echo '##topcpu'; ps -e -o pid= -o user= -o pcpu= -o pmem= -o rss= -o args= --sort=-pcpu 2>/dev/null; " +
        "echo '##topmem'; ps -e -o pid= -o user= -o pcpu= -o pmem= -o rss= -o args= --sort=-pmem 2>/dev/null";

    public async Task<CollectorResult> CollectAsync(SnapshotContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await context.ExecuteAsync(ProbeCommand, false, ct);
        sw.Stop();

        if (!result.Success && result.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(result) with { DurationMs = sw.Elapsed.TotalMilliseconds };
        if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
            return CollectorResult.FromCommandFailure(result) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var data = Parse(result.Output);
        if (data is null)
            return CollectorResult.Fail("态势输出解析失败(输出为空或格式未知)", result.ErrorKind) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        return CollectorResult.Ok(data) with { DurationMs = sw.Elapsed.TotalMilliseconds };
    }

    /// <summary>解析组合命令输出为态势结构；关键段(负载/内存)缺失时返回 null。</summary>
    public static Dictionary<string, object?>? Parse(string output)
    {
        var sections = SplitSections(output);
        if (!sections.TryGetValue("loadavg", out var loadRaw) || string.IsNullOrWhiteSpace(loadRaw))
            return null;

        var load = ParseLoadavg(loadRaw);
        if (load is null)
            return null;

        var data = new Dictionary<string, object?>
        {
            ["load"] = load,
            ["memory"] = ParseMemory(sections.GetValueOrDefault("mem") ?? string.Empty),
            ["disks"] = ParseDisks(sections.GetValueOrDefault("disk") ?? string.Empty),
            ["uptimeSeconds"] = ParseUptime(sections.GetValueOrDefault("uptime") ?? string.Empty),
            ["cpu"] = new Dictionary<string, object?>
            {
                ["model"] = AfterColon(sections.GetValueOrDefault("cpu")),
                ["cores"] = ParseIntOr(sections.GetValueOrDefault("cores"), 0)
            },
            ["os"] = ParseOsRelease(sections.GetValueOrDefault("os") ?? string.Empty),
            ["kernel"] = TrimOrNull(sections.GetValueOrDefault("kernel")),
            ["arch"] = TrimOrNull(sections.GetValueOrDefault("arch")),
            ["hostname"] = TrimOrNull(sections.GetValueOrDefault("host")),
            ["ips"] = (sections.GetValueOrDefault("ip") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ["topByCpu"] = ParseTopProcesses(sections.GetValueOrDefault("topcpu") ?? string.Empty, "cpu"),
            ["topByMemory"] = ParseTopProcesses(sections.GetValueOrDefault("topmem") ?? string.Empty, "mem")
        };
        data["warnings"] = BuildWarnings(data);
        data["riskLevel"] = data["warnings"] is List<Dictionary<string, object?>> ws && ws.Count > 0
            ? ws.Any(w => (string?)w["severity"] == "critical") ? "critical" : "warning"
            : "ok";
        return data;
    }

    /// <summary>解析 top 进程段（pid user pcpu pmem rss args...）；跳过非数字首列(表头/回显)与内核线程，
    /// 按 <paramref name="sortBy"/>（cpu/mem）降序、RSS 次序取前 5。</summary>
    public static List<Dictionary<string, object?>> ParseTopProcesses(string output, string sortBy = "cpu")
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = rawLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 6 || !long.TryParse(tokens[0], out var pid))
                continue;
            if (tokens[5].StartsWith('['))
                continue;   // 跳过内核线程([kworker] 等)与统计噪声

            list.Add(new Dictionary<string, object?>
            {
                ["pid"] = pid,
                ["user"] = tokens[1],
                ["cpuPercent"] = ToDouble(tokens[2]),
                ["memPercent"] = ToDouble(tokens[3]),
                ["rssMb"] = Math.Round(ToDouble(tokens[4]) / 1024.0, 1),
                ["command"] = string.Join(' ', tokens[5..])
            });
        }

        var ordered = sortBy == "mem"
            ? list.OrderByDescending(p => ToDouble(p["memPercent"])).ThenByDescending(p => ToDouble(p["rssMb"]))
            : list.OrderByDescending(p => ToDouble(p["cpuPercent"])).ThenByDescending(p => ToDouble(p["rssMb"]));
        return ordered.Take(5).ToList();
    }

    // 资源阈值（可按需调整；集中在此便于运维对照）
    public const double MemWarnPercent = 80, MemCritPercent = 90;
    public const double SwapWarnPercent = 50, SwapCritPercent = 80;
    public const double DiskWarnPercent = 80, DiskCritPercent = 90;
    public const double LoadWarnFactor = 2.0, LoadCritFactor = 4.0;

    /// <summary>按阈值生成告警列表（memory/swap/disk/load），供运维一眼看出异常。</summary>
    public static List<Dictionary<string, object?>> BuildWarnings(Dictionary<string, object?> data)
    {
        var warnings = new List<Dictionary<string, object?>>();
        void Add(string severity, string metric, string message, double value) =>
            warnings.Add(new Dictionary<string, object?>
            {
                ["severity"] = severity,
                ["metric"] = metric,
                ["message"] = message,
                ["value"] = value
            });

        if (data.GetValueOrDefault("memory") is Dictionary<string, object?> memory)
        {
            var used = ToDouble(memory.GetValueOrDefault("usedPercent"));
            if (used >= MemCritPercent)
                Add("critical", "memory", $"内存使用率 {used:F1}% (≥{MemCritPercent}%)", used);
            else if (used >= MemWarnPercent)
                Add("warning", "memory", $"内存使用率 {used:F1}% (≥{MemWarnPercent}%)", used);

            var swapTotal = ToDouble(memory.GetValueOrDefault("swapTotalMb"));
            var swap = ToDouble(memory.GetValueOrDefault("swapUsedPercent"));
            if (swapTotal > 0)
            {
                if (swap >= SwapCritPercent)
                    Add("critical", "swap", $"Swap 使用率 {swap:F1}% (≥{SwapCritPercent}%)", swap);
                else if (swap >= SwapWarnPercent)
                    Add("warning", "swap", $"Swap 使用率 {swap:F1}% (≥{SwapWarnPercent}%)", swap);
            }
        }

        if (data.GetValueOrDefault("disks") is List<Dictionary<string, object?>> disks)
        {
            foreach (var disk in disks)
            {
                var used = ToDouble(disk.GetValueOrDefault("usedPercent"));
                var mount = disk.GetValueOrDefault("mount") as string ?? disk.GetValueOrDefault("filesystem") as string ?? "?";
                if (used >= DiskCritPercent)
                    Add("critical", "disk", $"磁盘 {mount} 使用率 {used:F0}% (≥{DiskCritPercent}%)", used);
                else if (used >= DiskWarnPercent)
                    Add("warning", "disk", $"磁盘 {mount} 使用率 {used:F0}% (≥{DiskWarnPercent}%)", used);
            }
        }

        var cores = data.GetValueOrDefault("cpu") is Dictionary<string, object?> cpu ? ToDouble(cpu.GetValueOrDefault("cores")) : 0;
        if (cores > 0 && data.GetValueOrDefault("load") is Dictionary<string, object?> load)
        {
            var load1 = ToDouble(load.GetValueOrDefault("1m"));
            var ratio = load1 / cores;
            if (ratio >= LoadCritFactor)
                Add("critical", "load", $"1 分钟负载 {load1:F2} 达 {cores:F0} 核的 {ratio:F1} 倍", load1);
            else if (ratio >= LoadWarnFactor)
                Add("warning", "load", $"1 分钟负载 {load1:F2} 达 {cores:F0} 核的 {ratio:F1} 倍", load1);
        }

        return warnings;
    }

    /// <summary>分段标记必须"整行精确"匹配（仅 ## + 字母数字下划线），避免交互式 PTY 回显/换行
    /// 产生的以 ## 开头的命令碎片被误判为分段标记（如 su 提权时命令回显换行）。</summary>
    private static readonly Regex MarkerPattern = new("^##[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>按 <c>##key</c> 标记拆分组合输出；首个标记之前的前导输出挂在空键 <c>""</c> 下。</summary>
    public static Dictionary<string, string> SplitSections(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var key = string.Empty;   // 前导段（如直接执行的 ps/ss 输出）不丢弃
        var buffer = new System.Text.StringBuilder();

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (MarkerPattern.IsMatch(line))
            {
                result[key] = buffer.ToString();
                key = line[2..];
                buffer.Clear();
            }
            else
            {
                buffer.Append(line).Append('\n');   // 用 \n, 避免 AppendLine 在 Windows 上引入 \r 污染下游解析
            }
        }

        result[key] = buffer.ToString();
        return result;
    }

    /// <summary>"0.52 0.58 0.59 1/423 12345" → {1m,5m,15m,runnable,total}。</summary>
    public static Dictionary<string, object?>? ParseLoadavg(string raw)
    {
        var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l1) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var l5) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var l15))
            return null;

        long runnable = 0, total = 0;
        if (parts.Length > 3 && parts[3].Split('/') is { Length: 2 } rt &&
            long.TryParse(rt[0], out var r) && long.TryParse(rt[1], out var t))
        {
            runnable = r;
            total = t;
        }

        return new Dictionary<string, object?>
        {
            ["1m"] = l1, ["5m"] = l5, ["15m"] = l15,
            ["runnable"] = runnable, ["total"] = total
        };
    }

    /// <summary>解析 free -m（或回退的 /proc/meminfo）为 MB 数值。</summary>
    public static Dictionary<string, object?> ParseMemory(string raw)
    {
        long total = 0, used = 0, free = 0, available = 0, swapTotal = 0, swapUsed = 0;

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Mem:", StringComparison.Ordinal))
            {
                var v = ParseNumbers(trimmed["Mem:".Length..]);
                if (v.Count >= 2)
                {
                    total = v[0];
                    used = v[1];
                    if (v.Count > 2) free = v[2];
                    if (v.Count >= 6) available = v[5];   // total used free shared buff/cache available
                }
            }
            else if (trimmed.StartsWith("Swap:", StringComparison.Ordinal))
            {
                var v = ParseNumbers(trimmed["Swap:".Length..]);
                if (v.Count >= 2)
                {
                    swapTotal = v[0];
                    swapUsed = v[1];
                }
            }
            else if (trimmed.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = ParseIntOr(trimmed, 0) / 1024;          // kB → MB（free 不可用的回退）
            }
            else if (trimmed.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = ParseIntOr(trimmed, 0) / 1024;
            }
            else if (trimmed.StartsWith("MemFree:", StringComparison.Ordinal))
            {
                free = ParseIntOr(trimmed, 0) / 1024;
            }
            else if (trimmed.StartsWith("SwapTotal:", StringComparison.Ordinal))
            {
                swapTotal = ParseIntOr(trimmed, 0) / 1024;
            }
            else if (trimmed.StartsWith("SwapFree:", StringComparison.Ordinal))
            {
                swapUsed = Math.Max(0, swapTotal - ParseIntOr(trimmed, 0) / 1024);
            }
        }

        if (total <= 0)
            total = used + free;

        // 优先 (total-available) 口径（含 buff/cache 的真实压力），available 缺失时退回 used。
        var usedPercent = total > 0
            ? Math.Round((available > 0 ? total - available : used) * 100.0 / total, 1)
            : 0;
        var swapPercent = swapTotal > 0 ? Math.Round(swapUsed * 100.0 / swapTotal, 1) : 0;

        return new Dictionary<string, object?>
        {
            ["totalMb"] = total,
            ["usedMb"] = used,
            ["freeMb"] = free,
            ["availableMb"] = available,
            ["usedPercent"] = usedPercent,
            ["swapTotalMb"] = swapTotal,
            ["swapUsedMb"] = swapUsed,
            ["swapUsedPercent"] = swapPercent
        };
    }

    /// <summary>解析 df -hP（带 -P 保证每个文件系统单行，挂载点含空格也能正确取回）。</summary>
    public static List<Dictionary<string, object?>> ParseDisks(string raw)
    {
        var disks = new List<Dictionary<string, object?>>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Filesystem", StringComparison.Ordinal))
                continue;

            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 6)
                continue;

            disks.Add(new Dictionary<string, object?>
            {
                ["filesystem"] = tokens[0],
                ["sizeGb"] = ToGb(tokens[1]),
                ["usedGb"] = ToGb(tokens[2]),
                ["availGb"] = ToGb(tokens[3]),
                ["usedPercent"] = ParseIntOr(tokens[4].TrimEnd('%'), 0),
                ["mount"] = string.Join(' ', tokens[5..])
            });
        }
        return disks;
    }

    /// <summary>/proc/uptime 首字段 → 秒。</summary>
    public static double ParseUptime(string raw)
    {
        var first = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Round(seconds, 0)
            : 0;
    }

    /// <summary>/etc/os-release → {id, idLike, prettyName, versionId}。</summary>
    public static Dictionary<string, object?> ParseOsRelease(string raw)
    {
        string? id = null, idLike = null, pretty = null, versionId = null;
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            switch (key)
            {
                case "ID": id = value; break;
                case "ID_LIKE": idLike = value; break;
                case "PRETTY_NAME": pretty = value; break;
                case "VERSION_ID": versionId = value; break;
            }
        }
        return new Dictionary<string, object?>
        {
            ["id"] = id, ["idLike"] = idLike, ["prettyName"] = pretty, ["versionId"] = versionId
        };
    }

    /// <summary>"40G"/"1.5T"/"512M"/"981K"/"1023" → GB（double，保留 1 位小数）。</summary>
    public static double ToGb(string size)
    {
        if (string.IsNullOrWhiteSpace(size))
            return 0;
        var unit = char.ToUpperInvariant(size[^1]);
        var numberPart = unit is 'K' or 'M' or 'G' or 'T' or 'P' ? size[..^1] : size;
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0;

        var multiplier = unit switch
        {
            'K' => 1.0 / (1024 * 1024),
            'M' => 1.0 / 1024,
            'G' => 1.0,
            'T' => 1024.0,
            'P' => 1024.0 * 1024,
            _ => 1.0 / (1024 * 1024 * 1024)   // 纯字节数
        };
        return Math.Round(value * multiplier, 1);
    }

    private static List<long> ParseNumbers(string text)
    {
        var result = new List<long>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(token, out var value))
                result.Add(value);
            else
                break;   // 非数值即终止（free 的 shared/buff/cache 列在极老版本可能缺失）
        }
        return result;
    }

    private static int ParseIntOr(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        var start = 0;
        while (start < text.Length && !char.IsDigit(text[start]))
            start++;
        if (start >= text.Length)
            return fallback;

        var end = start;
        while (end < text.Length && char.IsDigit(text[end]))
            end++;
        return int.TryParse(text.AsSpan(start, end - start), out var value) ? value : fallback;
    }

    /// <summary>取冒号后的内容（"model name\t: Intel" → "Intel"）；无冒号则整体去空白。</summary>
    private static string? AfterColon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var colon = value.IndexOf(':');
        var text = colon >= 0 ? value[(colon + 1)..] : value;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double ToDouble(object? value) => value switch
    {
        null => 0,
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
        _ => 0
    };
}
