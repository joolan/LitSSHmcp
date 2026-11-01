using System.Globalization;
using LitSSHmcp.Core.Services.Snapshot.Collectors;

namespace LitSSHmcp.Core.Services.Resource;

public static class ResourceAnalysisParser
{
    public static ResourceAnalysisReport ParseQuick(string output)
    {
        var sections = ResourceSnapshotCollector.SplitSections(output);
        var report = new ResourceAnalysisReport { RawText = output };

        ParseMeta(Get(sections, "meta"), report);
        report.MemoryTotalMb = 0;
        var memory = ResourceSnapshotCollector.ParseMemory(Get(sections, "mem"));
        report.MemoryTotalMb = ToLong(memory, "totalMb");
        report.MemoryUsedMb = ToLong(memory, "usedMb");
        report.MemoryAvailableMb = ToLong(memory, "availableMb");
        report.SwapTotalMb = ToLong(memory, "swapTotalMb");
        report.SwapUsedMb = ToLong(memory, "swapUsedMb");
        report.MemoryUsedPercent = ToDouble(memory, "usedPercent");
        report.SwapUsedPercent = ToDouble(memory, "swapUsedPercent");

        var memLines = Get(sections, "mem").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var memLine = memLines.FirstOrDefault(l => l.TrimStart().StartsWith("Mem:", StringComparison.Ordinal));
        if (memLine is not null)
        {
            var values = ParseNumbers(memLine["Mem:".Length..]);
            if (values.Count >= 6)
            {
                report.MemoryCacheMb = values[4];
            }
        }

        UpdateCpu(Get(sections, "stat1"), Get(sections, "stat2"), report);
        UpdateLoad(Get(sections, "load"), report);
        report.VmstatText = TrimSection(Get(sections, "vmstat"));
        report.PsiText = TrimSection(Get(sections, "psi"));
        report.DiskIoText = TrimSection(Get(sections, "iostat"));
        report.OomText = TrimSection(Get(sections, "oom"));
        report.JournalUsage = TrimSection(Get(sections, "journal"));
        report.DockerUsage = TrimSection(Get(sections, "docker"));

        ParseDisks(Get(sections, "df"), Get(sections, "dfi"), Get(sections, "mounts"), report.Disks);

        var processes = ParseProcesses(Get(sections, "ps"));
        foreach (var process in processes.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.RssMb).Take(12))
        {
            report.TopCpuProcesses.Add(process);
        }

        foreach (var process in processes.OrderByDescending(p => p.RssMb).ThenByDescending(p => p.CpuPercent).Take(12))
        {
            if (!report.TopMemoryProcesses.Contains(process))
            {
                report.TopMemoryProcesses.Add(process);
            }
        }

        return report;
    }

    public static void ApplyAttribution(ResourceAnalysisReport report, string output)
    {
        var sections = ResourceSnapshotCollector.SplitSections(output);
        var docker = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Get(sections, "dockerps").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 2 && tokens[0].Length >= 6)
            {
                docker[tokens[0]] = tokens[1].Trim();
            }
        }

        var info = ParseAttributionInfo(Get(sections, "cgroups"));
        foreach (var process in report.TopCpuProcesses.Concat(report.TopMemoryProcesses).DistinctBy(p => p.Pid))
        {
            if (!info.TryGetValue(process.Pid, out var entry))
            {
                continue;
            }

            process.SwapMb = Math.Round(entry.SwapKb / 1024.0, 1);
            var lastSegment = entry.Cgroup.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            var containerId = ExtractContainerId(entry.Cgroup, lastSegment);
            if (containerId is not null &&
                docker.FirstOrDefault(x => containerId.StartsWith(x.Key, StringComparison.OrdinalIgnoreCase) ||
                                           x.Key.StartsWith(containerId, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } name)
            {
                process.Container = name;
                continue;
            }

            if (lastSegment.EndsWith(".service", StringComparison.OrdinalIgnoreCase))
            {
                process.Service = lastSegment[..^".service".Length];
            }
            else if (lastSegment.Contains("@", StringComparison.Ordinal) &&
                     lastSegment.EndsWith(".service", StringComparison.OrdinalIgnoreCase))
            {
                process.Service = lastSegment;
            }

        }
    }

    public static void ApplyDeepScan(ResourceAnalysisReport report, string output)
    {
        var sections = ResourceSnapshotCollector.SplitSections(output);
        report.DeepScanned = true;

        foreach (var line in Get(sections, "du").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0 || !long.TryParse(line[..tab], out var kb))
            {
                continue;
            }

            report.TopDirectories.Add(new ResourcePathSizeItem
            {
                Path = line[(tab + 1)..].Trim(),
                SizeMb = Math.Round(kb / 1024.0, 1)
            });
        }

        foreach (var line in Get(sections, "large").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0 || !long.TryParse(line[..tab], out var bytes))
            {
                continue;
            }

            report.LargeFiles.Add(new ResourcePathSizeItem
            {
                Path = line[(tab + 1)..].Trim(),
                SizeMb = Math.Round(bytes / 1024.0 / 1024.0, 1)
            });
        }

        foreach (var line in Get(sections, "deleted").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 4);
            if (parts.Length < 4 || !int.TryParse(parts[1], out var pid))
            {
                continue;
            }

            var size = ParseSizeToMb(parts[2]);
            report.DeletedFiles.Add(new ResourceDeletedFileItem
            {
                Command = parts[0].Trim(),
                Pid = pid,
                SizeMb = size,
                Path = parts[3].Trim()
            });
        }

        report.TopDirectories.Sort((a, b) => b.SizeMb.CompareTo(a.SizeMb));
        report.LargeFiles.Sort((a, b) => b.SizeMb.CompareTo(a.SizeMb));
        report.DeletedFiles.Sort((a, b) => b.SizeMb.CompareTo(a.SizeMb));

        var journal = TrimSection(Get(sections, "journal"));
        if (!string.IsNullOrWhiteSpace(journal))
        {
            report.JournalUsage = journal;
        }

        var docker = TrimSection(Get(sections, "docker"));
        if (!string.IsNullOrWhiteSpace(docker))
        {
            report.DockerUsage = docker;
        }

        report.RawText += "\n\n" + output;
    }

    public static IReadOnlyList<int> TopPids(ResourceAnalysisReport report)
        => report.TopCpuProcesses.Concat(report.TopMemoryProcesses)
            .Select(p => p.Pid)
            .Distinct()
            .Take(30)
            .ToArray();

    private static void ParseMeta(string text, ResourceAnalysisReport report)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd())
            .ToArray();
        if (lines.Length > 0)
        {
            report.HostName = lines[0].Trim();
        }

        if (lines.Length > 1 && DateTimeOffset.TryParse(lines[1], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var collectedAt))
        {
            report.CollectedAt = collectedAt;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("OS=", StringComparison.Ordinal))
            {
                report.OsName = line[3..].Trim();
            }
        }

        if (lines.Length > 3)
        {
            report.Kernel = lines[3].Trim();
        }

        if (lines.Length > 4 && int.TryParse(lines[4].Trim(), out var cores))
        {
            report.CpuCores = cores;
        }

        if (lines.Length > 5)
        {
            var model = lines[5];
            var colon = model.IndexOf(':');
            report.CpuModel = (colon >= 0 ? model[(colon + 1)..] : model).Trim();
        }

        report.MetaText = string.Join(" · ", new[]
        {
            report.HostName,
            report.OsName,
            string.IsNullOrWhiteSpace(report.Kernel) ? null : $"内核 {report.Kernel}",
            report.CpuCores > 0 ? $"{report.CpuCores} 核" : null,
            report.CpuModel
        }.Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    private static void UpdateCpu(string stat1, string stat2, ResourceAnalysisReport report)
    {
        var first = ParseCpuStat(stat1);
        var second = ParseCpuStat(stat2);
        if (first is null || second is null)
        {
            return;
        }

        var total = second.Value.Total - first.Value.Total;
        if (total <= 0)
        {
            return;
        }

        var iowait = second.Value.Iowait - first.Value.Iowait;
        var idle = second.Value.Idle - first.Value.Idle + iowait;
        var steal = second.Value.Steal - first.Value.Steal;
        report.CpuPercent = Math.Round(Math.Clamp(100.0 * (total - idle) / total, 0, 100), 1);
        report.IowaitPercent = Math.Round(Math.Clamp(100.0 * iowait / total, 0, 100), 1);
        report.StealPercent = Math.Round(Math.Clamp(100.0 * steal / total, 0, 100), 1);
    }

    private static void UpdateLoad(string text, ResourceAnalysisReport report)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return;
        }

        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l1))
        {
            report.Load1 = Math.Round(l1, 2);
        }

        if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var l5))
        {
            report.Load5 = Math.Round(l5, 2);
        }

        if (double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var l15))
        {
            report.Load15 = Math.Round(l15, 2);
        }
    }

    private static void ParseDisks(string df, string dfi, string mounts, List<ResourceDiskItem> disks)
    {
        var inodes = ParseInodeMap(dfi);
        var readOnly = ParseReadOnlyMap(mounts);

        foreach (var line in df.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Filesystem", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = trimmed.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 6)
            {
                continue;
            }

            var mount = tokens[5].Trim();
            disks.Add(new ResourceDiskItem
            {
                Filesystem = tokens[0],
                Size = tokens[1],
                Used = tokens[2],
                Available = tokens[3],
                UsedPercent = ParsePercent(tokens[4]),
                Mount = mount,
                InodePercent = inodes.TryGetValue(mount, out var inode) ? inode : 0,
                ReadOnly = readOnly.TryGetValue(mount, out var ro) && ro
            });
        }
    }

    private static Dictionary<string, double> ParseInodeMap(string text)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Trim().Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 6 || tokens[0] == "Filesystem")
            {
                continue;
            }

            result[tokens[5].Trim()] = ParsePercent(tokens[4]);
        }

        return result;
    }

    private static Dictionary<string, bool> ParseReadOnlyMap(string text)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            string target;
            string options;
            var tab = trimmed.IndexOf('\t');
            if (tab > 0)
            {
                target = trimmed[..tab];
                options = trimmed[(tab + 1)..];
            }
            else
            {
                var space = trimmed.IndexOf(' ');
                if (space <= 0)
                {
                    continue;
                }

                target = trimmed[..space];
                options = trimmed[(space + 1)..];
            }

            target = target.Replace("\\040", " ", StringComparison.Ordinal);
            var optionList = options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            result[target] = optionList.Contains("ro", StringComparer.Ordinal);
        }

        return result;
    }

    private static List<ResourceProcessItem> ParseProcesses(string text)
    {
        var result = new List<ResourceProcessItem>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Trim().Split((char[]?)null, 7, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 7 || !int.TryParse(tokens[0], out var pid))
            {
                continue;
            }

            if (tokens[6].StartsWith('['))
            {
                continue;
            }

            result.Add(new ResourceProcessItem
            {
                Pid = pid,
                User = tokens[1],
                CpuPercent = ToDouble(tokens[2]),
                MemPercent = ToDouble(tokens[3]),
                RssMb = Math.Round(ToDouble(tokens[4]) / 1024.0, 1),
                Elapsed = tokens[5],
                Command = tokens[6]
            });
        }

        return result;
    }

    private static Dictionary<int, AttributionEntry> ParseAttributionInfo(string text)
    {
        var result = new Dictionary<int, AttributionEntry>();
        AttributionEntry? current = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("##pid:", StringComparison.Ordinal) &&
                int.TryParse(line["##pid:".Length..], out var pid))
            {
                current = new AttributionEntry();
                result[pid] = current;
                continue;
            }

            if (current is null || line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("Name:", StringComparison.Ordinal))
            {
                current.Name = line["Name:".Length..].Trim();
            }
            else if (line.StartsWith("VmSwap:", StringComparison.Ordinal))
            {
                var number = line["VmSwap:".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var swap))
                {
                    current.SwapKb = swap;
                }
            }
            else if (!line.StartsWith("##", StringComparison.Ordinal))
            {
                current.Cgroup = line;
            }
        }

        return result;
    }



    private static string? ExtractContainerId(string cgroup, string lastSegment)
    {
        const string marker = "/docker/";
        var index = cgroup.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            var id = cgroup[(index + marker.Length)..].Trim('/');
            var slash = id.IndexOf('/');
            return slash >= 0 ? id[..slash] : id;
        }

        if (lastSegment.StartsWith("docker-", StringComparison.OrdinalIgnoreCase))
        {
            var id = lastSegment["docker-".Length..];
            return id.EndsWith(".scope", StringComparison.OrdinalIgnoreCase) ? id[..^".scope".Length] : id;
        }

        return null;
    }

    private static CpuStat? ParseCpuStat(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 5)
            {
                return null;
            }

            var values = new List<long>();
            foreach (var token in tokens.Skip(1))
            {
                if (!long.TryParse(token, out var value))
                {
                    break;
                }

                values.Add(value);
            }

            if (values.Count < 4)
            {
                return null;
            }

            return new CpuStat(
                values.Take(8).Sum(),
                values[3],
                values.Count > 4 ? values[4] : 0,
                values.Count > 7 ? values[7] : 0);
        }

        return null;
    }

    private static List<long> ParseNumbers(string text)
    {
        var result = new List<long>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(token, out var value))
            {
                result.Add(value);
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private static double ParsePercent(string value)
        => double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static double ParseSizeToMb(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return 0;
        }

        var unit = char.ToUpperInvariant(text[^1]);
        var numberPart = unit is 'K' or 'M' or 'G' or 'T' or 'B' ? text[..^1] : text;
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return 0;
        }

        return unit switch
        {
            'K' => Math.Round(number / 1024.0, 1),
            'M' => Math.Round(number, 1),
            'G' => Math.Round(number * 1024, 1),
            'T' => Math.Round(number * 1024 * 1024, 1),
            _ => Math.Round(number / 1024.0 / 1024.0, 1)
        };
    }

    private static string Get(Dictionary<string, string> sections, string key)
        => sections.TryGetValue(key, out var value) ? value : string.Empty;

    private static string TrimSection(string text)
        => text.Trim();

    private static long ToLong(Dictionary<string, object?> data, string key)
        => data.TryGetValue(key, out var value) && value is not null &&
           long.TryParse(value.ToString(), out var parsed)
            ? parsed
            : 0;

    private static double ToDouble(Dictionary<string, object?> data, string key)
        => data.TryGetValue(key, out var value) && value is not null &&
           double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static double ToDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private readonly record struct CpuStat(long Total, long Idle, long Iowait, long Steal);

    private sealed class AttributionEntry
    {
        public string Cgroup { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public double SwapKb { get; set; }
    }
}
