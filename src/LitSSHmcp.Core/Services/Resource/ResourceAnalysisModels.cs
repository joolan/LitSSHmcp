namespace LitSSHmcp.Core.Services.Resource;

public enum ResourceMetric
{
    Overview,
    Cpu,
    Memory,
    Disk
}

public enum ResourceSeverity
{
    Ok,
    Warning,
    Critical
}

public sealed class ResourceProcessItem
{
    public int Pid { get; init; }
    public string User { get; init; } = string.Empty;
    public double CpuPercent { get; init; }
    public double MemPercent { get; init; }
    public double RssMb { get; init; }
    public double SwapMb { get; set; }
    public string Elapsed { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string? Service { get; set; }
    public string? Container { get; set; }

    public string OwnerText => !string.IsNullOrWhiteSpace(Container)
        ? Container
        : !string.IsNullOrWhiteSpace(Service) ? Service : User;

    public string CpuText => $"{CpuPercent:0.0}%";
    public string MemText => $"{MemPercent:0.0}%";
    public string RssText => RssMb >= 1024 ? $"{RssMb / 1024:0.0} GB" : $"{RssMb:0} MB";
    public string SwapText => SwapMb <= 0 ? "-" : $"{SwapMb:0} MB";
}

public sealed class ResourceDiskItem
{
    public string Filesystem { get; init; } = string.Empty;
    public string Mount { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Used { get; init; } = string.Empty;
    public string Available { get; init; } = string.Empty;
    public double UsedPercent { get; init; }
    public double InodePercent { get; init; }
    public bool ReadOnly { get; init; }

    public string UsageText => $"{UsedPercent:0}%";
    public string InodeText => InodePercent > 0 ? $"{InodePercent:0}%" : "-";
    public string ReadOnlyText => ReadOnly ? "只读" : "可写";
    public string MountText => string.IsNullOrWhiteSpace(Mount) ? Filesystem : Mount;
}

public sealed class ResourcePathSizeItem
{
    public string Path { get; init; } = string.Empty;
    public double SizeMb { get; init; }
    public string SizeText => SizeMb >= 1024 ? $"{SizeMb / 1024:0.0} GB" : $"{SizeMb:0} MB";
}

public sealed class ResourceDeletedFileItem
{
    public int Pid { get; init; }
    public string Command { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public double SizeMb { get; init; }
    public string SizeText => SizeMb >= 1024 ? $"{SizeMb / 1024:0.0} GB" : $"{SizeMb:0} MB";
}

public sealed class ResourceFinding
{
    public ResourceMetric Metric { get; init; }
    public ResourceSeverity Severity { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string SeverityText => Severity switch
    {
        ResourceSeverity.Critical => "严重",
        ResourceSeverity.Warning => "警告",
        _ => "正常"
    };
}

public sealed class ResourceMetricSummary
{
    public ResourceMetric Metric { get; init; }
    public ResourceSeverity Severity { get; init; }
    public string Headline { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string SeverityText => Severity switch
    {
        ResourceSeverity.Critical => "严重",
        ResourceSeverity.Warning => "警告",
        _ => "正常"
    };
}

public sealed class ResourceAnalysisReport
{
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.Now;
    public string MetaText { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string OsName { get; set; } = string.Empty;
    public string Kernel { get; set; } = string.Empty;
    public string CpuModel { get; set; } = string.Empty;
    public int CpuCores { get; set; }
    public double CpuPercent { get; set; }
    public double IowaitPercent { get; set; }
    public double StealPercent { get; set; }
    public double Load1 { get; set; }
    public double Load5 { get; set; }
    public double Load15 { get; set; }
    public long MemoryTotalMb { get; set; }
    public long MemoryUsedMb { get; set; }
    public long MemoryAvailableMb { get; set; }
    public long MemoryCacheMb { get; set; }
    public long SwapTotalMb { get; set; }
    public long SwapUsedMb { get; set; }
    public double MemoryUsedPercent { get; set; }
    public double SwapUsedPercent { get; set; }
    public string VmstatText { get; set; } = string.Empty;
    public string PsiText { get; set; } = string.Empty;
    public string DiskIoText { get; set; } = string.Empty;
    public string OomText { get; set; } = string.Empty;
    public string JournalUsage { get; set; } = string.Empty;
    public string DockerUsage { get; set; } = string.Empty;
    public bool DeepScanned { get; set; }
    public string RawText { get; set; } = string.Empty;
    public List<ResourceProcessItem> TopCpuProcesses { get; } = new();
    public List<ResourceProcessItem> TopMemoryProcesses { get; } = new();
    public List<ResourceDiskItem> Disks { get; } = new();
    public List<ResourcePathSizeItem> TopDirectories { get; } = new();
    public List<ResourcePathSizeItem> LargeFiles { get; } = new();
    public List<ResourceDeletedFileItem> DeletedFiles { get; } = new();
    public List<ResourceFinding> Findings { get; } = new();
    public Dictionary<ResourceMetric, ResourceMetricSummary> Summaries { get; } = new();

    public ResourceMetricSummary GetSummary(ResourceMetric metric)
        => Summaries.TryGetValue(metric, out var summary)
            ? summary
            : new ResourceMetricSummary { Metric = metric, Headline = "暂无数据" };
}
