namespace LitSSHmcp.Core.Services.Resource;

public static class ResourceAnalysisAdvisor
{
    public static void Apply(ResourceAnalysisReport report)
    {
        report.Findings.Clear();
        report.Summaries.Clear();

        ApplyCpu(report);
        ApplyMemory(report);
        ApplyDisk(report);
        ApplyOverview(report);
    }

    private static void ApplyCpu(ResourceAnalysisReport report)
    {
        var findings = new List<ResourceFinding>();
        var severity = ResourceSeverity.Ok;

        if (report.CpuPercent >= 90)
        {
            severity = ResourceSeverity.Critical;
            findings.Add(Finding(ResourceMetric.Cpu, ResourceSeverity.Critical,
                "CPU 使用率严重偏高", $"当前采样 CPU 使用率 {report.CpuPercent:0.0}%"));
        }
        else if (report.CpuPercent >= 70)
        {
            severity = ResourceSeverity.Warning;
            findings.Add(Finding(ResourceMetric.Cpu, ResourceSeverity.Warning,
                "CPU 使用率偏高", $"当前采样 CPU 使用率 {report.CpuPercent:0.0}%"));
        }

        var cores = Math.Max(1, report.CpuCores);
        var loadRatio = report.Load1 / cores;
        if (loadRatio >= 4)
        {
            severity = ResourceSeverity.Critical;
            findings.Add(Finding(ResourceMetric.Cpu, ResourceSeverity.Critical,
                "系统负载严重偏高",
                $"1 分钟负载 {report.Load1:0.00}，约为 {cores} 核的 {loadRatio:0.0} 倍"));
        }
        else if (loadRatio >= 2)
        {
            severity = Max(severity, ResourceSeverity.Warning);
            findings.Add(Finding(ResourceMetric.Cpu, ResourceSeverity.Warning,
                "系统负载偏高",
                $"1 分钟负载 {report.Load1:0.00}，约为 {cores} 核的 {loadRatio:0.0} 倍"));
        }

        if (report.IowaitPercent >= 20)
        {
            severity = Max(severity, report.IowaitPercent >= 40 ? ResourceSeverity.Critical : ResourceSeverity.Warning);
            findings.Add(Finding(ResourceMetric.Cpu,
                report.IowaitPercent >= 40 ? ResourceSeverity.Critical : ResourceSeverity.Warning,
                "IO 等待偏高",
                $"iowait {report.IowaitPercent:0.0}%，CPU 可能被磁盘/网络 IO 阻塞"));
        }

        if (report.StealPercent >= 10)
        {
            severity = Max(severity, ResourceSeverity.Warning);
            findings.Add(Finding(ResourceMetric.Cpu, ResourceSeverity.Warning,
                "CPU steal 偏高",
                $"steal {report.StealPercent:0.0}%，虚拟化宿主机可能存在资源争抢"));
        }

        var top = report.TopCpuProcesses.FirstOrDefault();
        if (top is not null && top.CpuPercent >= 50)
        {
            severity = Max(severity, top.CpuPercent >= 100 ? ResourceSeverity.Critical : ResourceSeverity.Warning);
            findings.Add(Finding(ResourceMetric.Cpu,
                top.CpuPercent >= 100 ? ResourceSeverity.Critical : ResourceSeverity.Warning,
                "存在 CPU 占用大户",
                $"{top.Command}（PID {top.Pid}，{top.User}）累计 CPU {top.CpuPercent:0.0}%，内存占比 {top.MemPercent:0.0}%"));
        }

        var headline = report.CpuCores > 0
            ? $"CPU {report.CpuPercent:0.0}% · 负载 {report.Load1:0.00}/{report.Load5:0.00}/{report.Load15:0.00} · {report.CpuCores} 核"
            : $"CPU {report.CpuPercent:0.0}%";
        var detail = top is null
            ? "未读取到进程 CPU 数据。"
            : $"Top CPU：{top.Command}（PID {top.Pid}，{top.OwnerText}）累计 {top.CpuPercent:0.0}%。iowait {report.IowaitPercent:0.0}%，steal {report.StealPercent:0.0}%。";

        report.Summaries[ResourceMetric.Cpu] = new ResourceMetricSummary
        {
            Metric = ResourceMetric.Cpu,
            Severity = severity,
            Headline = headline,
            Detail = detail
        };
        report.Findings.AddRange(findings);
    }

    private static void ApplyMemory(ResourceAnalysisReport report)
    {
        var findings = new List<ResourceFinding>();
        var severity = ResourceSeverity.Ok;

        if (report.MemoryUsedPercent >= 85)
        {
            severity = ResourceSeverity.Critical;
            findings.Add(Finding(ResourceMetric.Memory, ResourceSeverity.Critical,
                "内存使用率严重偏高",
                $"已使用 {report.MemoryUsedPercent:0.0}%（{FormatMb(report.MemoryUsedMb)} / {FormatMb(report.MemoryTotalMb)}），可用 {FormatMb(report.MemoryAvailableMb)}"));
        }
        else if (report.MemoryUsedPercent >= 70)
        {
            severity = ResourceSeverity.Warning;
            findings.Add(Finding(ResourceMetric.Memory, ResourceSeverity.Warning,
                "内存使用率偏高",
                $"已使用 {report.MemoryUsedPercent:0.0}%（{FormatMb(report.MemoryUsedMb)} / {FormatMb(report.MemoryTotalMb)}），可用 {FormatMb(report.MemoryAvailableMb)}"));
        }

        if (report.SwapTotalMb > 0)
        {
            if (report.SwapUsedPercent >= 80)
            {
                severity = ResourceSeverity.Critical;
                findings.Add(Finding(ResourceMetric.Memory, ResourceSeverity.Critical,
                    "Swap 使用率严重偏高",
                    $"Swap 已使用 {report.SwapUsedPercent:0.0}%（{FormatMb(report.SwapUsedMb)} / {FormatMb(report.SwapTotalMb)}），可能已发生明显换页"));
            }
            else if (report.SwapUsedPercent >= 50)
            {
                severity = Max(severity, ResourceSeverity.Warning);
                findings.Add(Finding(ResourceMetric.Memory, ResourceSeverity.Warning,
                    "Swap 使用率偏高",
                    $"Swap 已使用 {report.SwapUsedPercent:0.0}%（{FormatMb(report.SwapUsedMb)} / {FormatMb(report.SwapTotalMb)}）"));
            }
        }

        if (!string.IsNullOrWhiteSpace(report.OomText))
        {
            severity = ResourceSeverity.Critical;
            findings.Add(Finding(ResourceMetric.Memory, ResourceSeverity.Critical,
                "检测到 OOM 记录", FirstLines(report.OomText, 3)));
        }

        var top = report.TopMemoryProcesses.FirstOrDefault();
        if (top is not null && top.RssMb >= 1024)
        {
            severity = Max(severity, top.RssMb >= 4096 ? ResourceSeverity.Critical : ResourceSeverity.Warning);
            findings.Add(Finding(ResourceMetric.Memory,
                top.RssMb >= 4096 ? ResourceSeverity.Critical : ResourceSeverity.Warning,
                "存在内存占用大户",
                $"{top.Command}（PID {top.Pid}，{top.User}）RSS {top.RssText}，内存占比 {top.MemPercent:0.0}%"));
        }

        var headline = $"内存 {report.MemoryUsedPercent:0.0}% · {FormatMb(report.MemoryUsedMb)}/{FormatMb(report.MemoryTotalMb)} · 可用 {FormatMb(report.MemoryAvailableMb)}";
        var detail = top is null
            ? "未读取到进程内存数据。"
            : $"Top RSS：{top.Command}（PID {top.Pid}，{top.OwnerText}）{top.RssText}。Swap 使用 {report.SwapUsedPercent:0.0}%。";
        if (!string.IsNullOrWhiteSpace(report.PsiText))
        {
            detail += " 已读取 PSI 内存压力数据。";
        }

        report.Summaries[ResourceMetric.Memory] = new ResourceMetricSummary
        {
            Metric = ResourceMetric.Memory,
            Severity = severity,
            Headline = headline,
            Detail = detail
        };
        report.Findings.AddRange(findings);
    }

    private static void ApplyDisk(ResourceAnalysisReport report)
    {
        var findings = new List<ResourceFinding>();
        var severity = ResourceSeverity.Ok;

        foreach (var disk in report.Disks)
        {
            if (disk.ReadOnly)
            {
                severity = Max(severity, ResourceSeverity.Critical);
                findings.Add(Finding(ResourceMetric.Disk, ResourceSeverity.Critical,
                    $"挂载点只读：{disk.MountText}",
                    $"{disk.Filesystem} 挂载为只读，写入可能失败；请检查文件系统错误或磁盘故障"));
            }

            if (disk.UsedPercent >= 90)
            {
                severity = ResourceSeverity.Critical;
                findings.Add(Finding(ResourceMetric.Disk, ResourceSeverity.Critical,
                    $"磁盘使用率严重偏高：{disk.MountText}",
                    $"已使用 {disk.UsedPercent:0}%（{disk.Used}/{disk.Size}），可用 {disk.Available}"));
            }
            else if (disk.UsedPercent >= 80)
            {
                severity = Max(severity, ResourceSeverity.Warning);
                findings.Add(Finding(ResourceMetric.Disk, ResourceSeverity.Warning,
                    $"磁盘使用率偏高：{disk.MountText}",
                    $"已使用 {disk.UsedPercent:0}%（{disk.Used}/{disk.Size}），可用 {disk.Available}"));
            }

            if (disk.InodePercent >= 90)
            {
                severity = ResourceSeverity.Critical;
                findings.Add(Finding(ResourceMetric.Disk, ResourceSeverity.Critical,
                    $"inode 使用率严重偏高：{disk.MountText}",
                    $"inode 已使用 {disk.InodePercent:0}%，可能出现“磁盘有空间但无法创建文件”"));
            }
            else if (disk.InodePercent >= 80)
            {
                severity = Max(severity, ResourceSeverity.Warning);
                findings.Add(Finding(ResourceMetric.Disk, ResourceSeverity.Warning,
                    $"inode 使用率偏高：{disk.MountText}",
                    $"inode 已使用 {disk.InodePercent:0}%"));
            }
        }

        var biggestDir = report.TopDirectories.FirstOrDefault();
        if (biggestDir is not null)
        {
            findings.Add(Finding(ResourceMetric.Disk,
                biggestDir.SizeMb >= 10240 ? ResourceSeverity.Warning : ResourceSeverity.Ok,
                "最大目录", $"{biggestDir.Path} 占用 {biggestDir.SizeText}"));
        }

        var biggestFile = report.LargeFiles.FirstOrDefault();
        if (biggestFile is not null && biggestFile.SizeMb >= 1024)
        {
            findings.Add(Finding(ResourceMetric.Disk, ResourceSeverity.Warning,
                "大文件", $"{biggestFile.Path} 大小 {biggestFile.SizeText}"));
        }

        var deleted = report.DeletedFiles.FirstOrDefault();
        if (deleted is not null && deleted.SizeMb >= 1024)
        {
            severity = Max(severity, ResourceSeverity.Warning);
            findings.Add(Finding(ResourceMetric.Disk, ResourceSeverity.Warning,
                "存在已删除但仍被占用的文件",
                $"{deleted.Path}（{deleted.Command}，PID {deleted.Pid}）占用 {deleted.SizeText}，释放空间需重启或安全重启对应进程"));
        }

        var worst = report.Disks.OrderByDescending(d => d.UsedPercent).FirstOrDefault();
        var headline = worst is null
            ? "未读取到磁盘数据"
            : $"磁盘最高使用率 {worst.UsedPercent:0}% · {worst.MountText}";
        var detail = worst is null
            ? "无法读取 df 数据。"
            : $"{worst.MountText}：{worst.Used}/{worst.Size}，可用 {worst.Available}，inode {worst.InodeText}。"
              + (biggestDir is null ? string.Empty : $" 最大目录 {biggestDir.Path}（{biggestDir.SizeText}）。")
              + (report.DeepScanned ? string.Empty : " 尚未执行深度扫描。");

        report.Summaries[ResourceMetric.Disk] = new ResourceMetricSummary
        {
            Metric = ResourceMetric.Disk,
            Severity = severity,
            Headline = headline,
            Detail = detail
        };
        report.Findings.AddRange(findings);
    }

    private static void ApplyOverview(ResourceAnalysisReport report)
    {
        var severity = new[]
        {
            report.GetSummary(ResourceMetric.Cpu).Severity,
            report.GetSummary(ResourceMetric.Memory).Severity,
            report.GetSummary(ResourceMetric.Disk).Severity
        }.Max();

        var problems = report.Findings
            .Where(f => f.Severity != ResourceSeverity.Ok)
            .Select(f => f.Title)
            .Distinct()
            .Take(4)
            .ToArray();

        var headline = severity switch
        {
            ResourceSeverity.Critical => "存在严重资源风险",
            ResourceSeverity.Warning => "存在资源风险",
            _ => "资源状态正常"
        };

        report.Summaries[ResourceMetric.Overview] = new ResourceMetricSummary
        {
            Metric = ResourceMetric.Overview,
            Severity = severity,
            Headline = headline,
            Detail = problems.Length == 0
                ? "CPU、内存、磁盘均未发现明显异常。"
                : string.Join("；", problems)
        };
    }

    private static ResourceFinding Finding(ResourceMetric metric, ResourceSeverity severity, string title, string detail)
        => new()
        {
            Metric = metric,
            Severity = severity,
            Title = title,
            Detail = detail
        };

    private static ResourceSeverity Max(ResourceSeverity left, ResourceSeverity right)
        => left >= right ? left : right;

    private static string FormatMb(long mb)
        => mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb} MB";

    private static string FirstLines(string text, int count)
        => string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(count)
            .Select(l => l.Trim()));
}
