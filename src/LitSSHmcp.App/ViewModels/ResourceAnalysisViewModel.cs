using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Resource;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

public sealed class ResourceAnalysisViewModel : INotifyPropertyChanged
{
    private readonly SshServerConfig _server;
    private readonly ISshService _ssh;
    private readonly IUserDialogService _dialogs;
    private readonly Func<string, bool, Task<bool>>? _sendToAgent;

    private ResourceAnalysisReport? _report;
    private ResourceMetric _currentMetric = ResourceMetric.Overview;
    private bool _busy;
    private bool _deepScanning;
    private string _statusMessage = "正在采集资源数据…";

    public ResourceAnalysisViewModel(
        SshServerConfig server,
        ISshService ssh,
        IUserDialogService dialogs,
        Func<string, bool, Task<bool>>? sendToAgent = null)
    {
        _server = server;
        _ssh = ssh;
        _dialogs = dialogs;
        _sendToAgent = sendToAgent;

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsBusy && !IsDeepScanning);
        DeepScanCommand = new RelayCommand(_ => _ = DeepScanAsync(), _ => !IsBusy && !IsDeepScanning && Report is not null);
    }

    public SshServerConfig Server => _server;
    public string Title => $"资源占用分析 - {_server.Name}";
    public string Subtitle => $"{_server.Host}:{_server.Port}";

    public ICommand RefreshCommand { get; }
    public ICommand DeepScanCommand { get; }

    public ObservableCollection<ResourceProcessItem> TopCpuProcesses { get; } = new();
    public ObservableCollection<ResourceProcessItem> TopMemoryProcesses { get; } = new();
    public ObservableCollection<ResourceDiskItem> Disks { get; } = new();
    public ObservableCollection<ResourcePathSizeItem> TopDirectories { get; } = new();
    public ObservableCollection<ResourcePathSizeItem> LargeFiles { get; } = new();
    public ObservableCollection<ResourceDeletedFileItem> DeletedFiles { get; } = new();
    public ObservableCollection<ResourceFinding> Findings { get; } = new();

    public ResourceAnalysisReport? Report
    {
        get => _report;
        private set
        {
            if (ReferenceEquals(_report, value)) return;
            _report = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReport));
            OnPropertyChanged(nameof(MetaText));
            OnPropertyChanged(nameof(CollectedAtText));
            OnPropertyChanged(nameof(HasDeepData));
            OnPropertyChanged(nameof(HasDeletedFiles));
            OnPropertyChanged(nameof(HasDirectories));
            OnPropertyChanged(nameof(HasLargeFiles));
            RaiseCommandStates();
        }
    }

    public bool HasReport => Report is not null;
    public string MetaText => Report?.MetaText ?? string.Empty;
    public string CollectedAtText => Report is null
        ? string.Empty
        : $"采集时间：{Report.CollectedAt:yyyy-MM-dd HH:mm:ss} · {(Report.DeepScanned ? "含深度扫描" : "快速分析")}";

    public ResourceMetric CurrentMetric
    {
        get => _currentMetric;
        set
        {
            if (_currentMetric == value) return;
            _currentMetric = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentSummary));
            OnPropertyChanged(nameof(CurrentHeadline));
            OnPropertyChanged(nameof(CurrentDetail));
            OnPropertyChanged(nameof(CurrentSeverityText));
        }
    }

    public ResourceMetricSummary CurrentSummary => Report?.GetSummary(CurrentMetric)
        ?? new ResourceMetricSummary { Metric = CurrentMetric, Headline = "暂无数据" };
    public string CurrentHeadline => CurrentSummary.Headline;
    public string CurrentDetail => CurrentSummary.Detail;
    public string CurrentSeverityText => CurrentSummary.SeverityText;

    public string OverviewHeadline => Report?.GetSummary(ResourceMetric.Overview).Headline ?? "暂无数据";
    public string OverviewDetail => Report?.GetSummary(ResourceMetric.Overview).Detail ?? "点击刷新开始分析。";
    public string OverviewSeverityText => Report?.GetSummary(ResourceMetric.Overview).SeverityText ?? "未知";

    public string CpuHeadline => Report?.GetSummary(ResourceMetric.Cpu).Headline ?? "暂无数据";
    public string CpuDetail => Report?.GetSummary(ResourceMetric.Cpu).Detail ?? string.Empty;
    public string CpuSeverityText => Report?.GetSummary(ResourceMetric.Cpu).SeverityText ?? "未知";

    public string MemoryHeadline => Report?.GetSummary(ResourceMetric.Memory).Headline ?? "暂无数据";
    public string MemoryDetail => Report?.GetSummary(ResourceMetric.Memory).Detail ?? string.Empty;
    public string MemorySeverityText => Report?.GetSummary(ResourceMetric.Memory).SeverityText ?? "未知";

    public string DiskHeadline => Report?.GetSummary(ResourceMetric.Disk).Headline ?? "暂无数据";
    public string DiskDetail => Report?.GetSummary(ResourceMetric.Disk).Detail ?? string.Empty;
    public string DiskSeverityText => Report?.GetSummary(ResourceMetric.Disk).SeverityText ?? "未知";

    public bool HasDeepData => Report?.DeepScanned == true;
    public bool HasDeletedFiles => DeletedFiles.Count > 0;
    public bool HasDirectories => TopDirectories.Count > 0;
    public bool HasLargeFiles => LargeFiles.Count > 0;

    public string JournalUsage => Report?.JournalUsage ?? string.Empty;
    public string DockerUsage => Report?.DockerUsage ?? string.Empty;
    public string OomText => Report?.OomText ?? string.Empty;
    public string DiskIoText => Report?.DiskIoText ?? string.Empty;
    public bool HasOom => !string.IsNullOrWhiteSpace(Report?.OomText);

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (_busy == value) return;
            _busy = value;
            OnPropertyChanged();
            RaiseCommandStates();
        }
    }

    public bool IsDeepScanning
    {
        get => _deepScanning;
        private set
        {
            if (_deepScanning == value) return;
            _deepScanning = value;
            OnPropertyChanged();
            RaiseCommandStates();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value) return;
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public async Task RefreshAsync()
    {
        if (IsBusy || IsDeepScanning)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "正在采集 CPU / 内存 / 磁盘数据…";
        try
        {
            var quick = await _ssh.ExecuteCommandAsync(_server, ResourceAnalysisCommands.Quick,
                timeoutSeconds: 70);
            if (!quick.Success && string.IsNullOrWhiteSpace(quick.Output))
            {
                StatusMessage = "采集失败：" + (string.IsNullOrWhiteSpace(quick.Error) ? "无输出" : quick.Error);
                return;
            }

            var report = ResourceAnalysisParser.ParseQuick(quick.Output);
            var pids = ResourceAnalysisParser.TopPids(report);
            if (pids.Count > 0)
            {
                var attribution = await _ssh.ExecuteCommandAsync(_server,
                    ResourceAnalysisCommands.BuildAttribution(pids), timeoutSeconds: 30);
                if (attribution.Success || !string.IsNullOrWhiteSpace(attribution.Output))
                {
                    ResourceAnalysisParser.ApplyAttribution(report, attribution.Output);
                }
            }

            ResourceAnalysisAdvisor.Apply(report);
            Report = report;
            RefreshCollections(report);
            StatusMessage = $"更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusMessage = "采集失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeepScanAsync()
    {
        if (Report is null || IsBusy || IsDeepScanning)
        {
            return;
        }

        IsDeepScanning = true;
        StatusMessage = "正在执行磁盘深度扫描（最多约 1–2 分钟）…";
        try
        {
            var result = await _ssh.ExecuteCommandAsync(_server, ResourceAnalysisCommands.DiskDeep,
                timeoutSeconds: 100);
            if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
            {
                StatusMessage = "深度扫描失败：" + (string.IsNullOrWhiteSpace(result.Error) ? "无输出" : result.Error);
                return;
            }

            var report = Report;
            ResourceAnalysisParser.ApplyDeepScan(report, result.Output);
            ResourceAnalysisAdvisor.Apply(report);
            RefreshCollections(report);
            OnPropertyChanged(nameof(Report));
            OnPropertyChanged(nameof(MetaText));
            OnPropertyChanged(nameof(CollectedAtText));
            StatusMessage = $"深度扫描完成：{DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusMessage = "深度扫描失败：" + ex.Message;
        }
        finally
        {
            IsDeepScanning = false;
        }
    }

    public async Task<string?> BuildAiPromptAsync(ResourceMetric metric)
    {
        if (Report is null)
        {
            await RefreshAsync();
        }

        var report = Report;
        if (report is null)
        {
            return null;
        }

        var isOverview = metric == ResourceMetric.Overview;
        var focus = metric switch
        {
            ResourceMetric.Cpu => "CPU 占用",
            ResourceMetric.Memory => "内存占用",
            ResourceMetric.Disk => "磁盘占用",
            _ => "整体资源占用"
        };

        var builder = new StringBuilder();
        builder.AppendLine($"请分析以下 Linux 服务器的{focus}情况，目标是评估该项是否异常、找出占用大户并给出只读排查建议。");
        builder.AppendLine("允许并鼓励使用只读 MCP 工具、只读命令、日志/进程/容器/挂载点/大文件/网络连接等追溯信息；不要修改文件、权限、服务或配置，不要清理、重启或终止进程。");
        builder.AppendLine();
        builder.AppendLine("## 服务器信息");
        builder.AppendLine($"- 名称：{_server.Name}");
        builder.AppendLine($"- serverId：{_server.Id}");
        builder.AppendLine($"- 地址：{_server.Host}:{_server.Port}");
        builder.AppendLine($"- 登录用户：{_server.Username}");
        builder.AppendLine($"- 系统：{report.OsName}");
        builder.AppendLine($"- 主机名：{report.HostName}");
        builder.AppendLine($"- 内核：{report.Kernel}");
        builder.AppendLine($"- CPU：{report.CpuModel}（{report.CpuCores} 核）");
        builder.AppendLine($"- 采集时间：{report.CollectedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 本次分析范围：{focus}");

        builder.AppendLine();
        builder.AppendLine("## 关键指标");
        if (isOverview || metric == ResourceMetric.Cpu)
        {
            builder.AppendLine($"- CPU 使用率：{report.CpuPercent:0.0}% · iowait {report.IowaitPercent:0.0}% · steal {report.StealPercent:0.0}%");
            builder.AppendLine($"- 负载：{report.Load1:0.00} / {report.Load5:0.00} / {report.Load15:0.00} · {report.CpuCores} 核");
        }

        if (isOverview || metric == ResourceMetric.Memory)
        {
            builder.AppendLine($"- 内存使用：{report.MemoryUsedPercent:0.0}%（{FormatMb(report.MemoryUsedMb)} / {FormatMb(report.MemoryTotalMb)}）· 可用 {FormatMb(report.MemoryAvailableMb)} · cache {FormatMb(report.MemoryCacheMb)}");
            builder.AppendLine($"- Swap：{report.SwapUsedPercent:0.0}%（{FormatMb(report.SwapUsedMb)} / {FormatMb(report.SwapTotalMb)}）");
        }

        if (isOverview || metric == ResourceMetric.Disk)
        {
            builder.AppendLine($"- 磁盘：{report.GetSummary(ResourceMetric.Disk).Headline}");
            foreach (var disk in report.Disks)
            {
                builder.AppendLine($"  - {disk.MountText}（{disk.Filesystem}）：{disk.Used}/{disk.Size}，使用 {disk.UsedPercent:0}%，inode {disk.InodeText}，{disk.ReadOnlyText}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 当前结论");
        var summary = report.GetSummary(metric);
        builder.AppendLine($"- {summary.SeverityText}：{summary.Headline}");
        builder.AppendLine($"- {summary.Detail}");

        if (isOverview || metric == ResourceMetric.Cpu)
        {
            AppendProcessTable(builder, "Top CPU 进程（ps 累计 CPU%）", report.TopCpuProcesses);
        }

        if (isOverview || metric == ResourceMetric.Memory)
        {
            AppendProcessTable(builder, "Top 内存进程", report.TopMemoryProcesses);
        }

        if (isOverview || metric == ResourceMetric.Disk)
        {
            if (report.TopDirectories.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## 最大目录（深度扫描）");
                foreach (var item in report.TopDirectories.Take(20))
                {
                    builder.AppendLine($"- {item.Path}：{item.SizeText}");
                }
            }

            if (report.LargeFiles.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## 大文件（深度扫描）");
                foreach (var item in report.LargeFiles.Take(20))
                {
                    builder.AppendLine($"- {item.Path}：{item.SizeText}");
                }
            }

            if (report.DeletedFiles.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## 已删除但仍被占用的文件");
                foreach (var item in report.DeletedFiles.Take(20))
                {
                    builder.AppendLine($"- {item.Path}：{item.SizeText}（{item.Command}，PID {item.Pid}）");
                }
            }

            if (!string.IsNullOrWhiteSpace(report.JournalUsage))
            {
                builder.AppendLine();
                builder.AppendLine("## journal 占用");
                builder.AppendLine(Truncate(report.JournalUsage, 4000));
            }

            if (!string.IsNullOrWhiteSpace(report.DockerUsage))
            {
                builder.AppendLine();
                builder.AppendLine("## Docker 占用");
                builder.AppendLine(Truncate(report.DockerUsage, 6000));
            }

            if (!string.IsNullOrWhiteSpace(report.DiskIoText))
            {
                builder.AppendLine();
                builder.AppendLine("## 磁盘 IO（iostat）");
                builder.AppendLine("```text");
                builder.AppendLine(Truncate(report.DiskIoText, 6000));
                builder.AppendLine("```");
            }
        }

        var findings = (isOverview
                ? report.Findings
                : report.Findings.Where(f => f.Metric == metric))
            .Where(f => f.Severity != ResourceSeverity.Ok)
            .Take(30)
            .ToArray();
        if (findings.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 告警与发现");
            foreach (var finding in findings)
            {
                builder.AppendLine($"- [{finding.SeverityText}] {finding.Title}：{finding.Detail}");
            }
        }

        if ((isOverview || metric == ResourceMetric.Memory) && !string.IsNullOrWhiteSpace(report.OomText))
        {
            builder.AppendLine();
            builder.AppendLine("## OOM 记录");
            builder.AppendLine(Truncate(report.OomText, 4000));
        }

        builder.AppendLine();
        builder.AppendLine("## 原始数据（仅当前分析范围相关，截断）");
        builder.AppendLine("```text");
        builder.AppendLine(Truncate(SelectRawForMetric(report.RawText, metric), 50000));
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("请重点分析：");
        switch (metric)
        {
            case ResourceMetric.Cpu:
                builder.AppendLine("1. CPU 使用率、load、iowait、steal 是否异常，是否存在 CPU 饱和或 IO/虚拟化争抢。");
                builder.AppendLine("2. Top CPU 进程/服务/容器是谁，占用是否符合业务预期；是否存在死循环、频繁 GC、备份/编译等短期高峰。");
                builder.AppendLine("3. 如需继续追溯，请只读检查线程、服务、容器、日志、网络连接或相关配置。");
                builder.AppendLine("4. 给出 CPU 优化建议（限流、调度、扩容、定位热点等），不要直接执行修改。");
                break;
            case ResourceMetric.Memory:
                builder.AppendLine("1. used/available/cache/swap、OOM、PSI 是否显示内存压力或泄漏迹象。");
                builder.AppendLine("2. Top RSS/Swap 进程/服务/容器是谁；是否属于缓存、JVM/数据库/容器限制或异常增长。");
                builder.AppendLine("3. 如需继续追溯，请只读检查 /proc、进程状态、服务/容器、日志和 OOM 记录。");
                builder.AppendLine("4. 给出内存优化建议（扩容、限制、调整堆/缓存/容器配额等），不要直接执行修改。");
                break;
            case ResourceMetric.Disk:
                builder.AppendLine("1. 哪个挂载点、inode、只读挂载、已删除未释放文件或磁盘 IO 存在异常，是否存在写满风险。");
                builder.AppendLine("2. 占用大户是哪些目录/文件/进程/容器；journal、Docker、日志是否异常增长；是否有已删除但仍被占用的文件。");
                builder.AppendLine("3. 如需继续追溯，请只读检查 du/find/lsof/iostat/journalctl/docker 等数据。");
                builder.AppendLine("4. 给出磁盘治理建议（日志轮转、清理策略、扩容、inode 治理等），不要直接执行修改。");
                break;
            default:
                builder.AppendLine("1. CPU、内存、磁盘三项指标及其相互作用（如 iowait 与磁盘 IO、swap 与内存、load 与 CPU）是否异常。");
                builder.AppendLine("2. 整体占用大户分别是谁（进程、用户、systemd 服务、Docker 容器），是否存在跨指标关联风险。");
                builder.AppendLine("3. 是否存在 OOM、磁盘写满、inode 耗尽、只读挂载、IO 瓶颈、日志/Docker 膨胀等风险。");
                builder.AppendLine("4. 如需继续追溯，请直接调用只读工具或给出具体只读命令。");
                builder.AppendLine("5. 给出优先级明确的整体处理建议，但不要直接执行修改、清理、重启或终止进程。");
                break;
        }

        return builder.ToString();
    }

    private static string SelectRawForMetric(string raw, ResourceMetric metric)
    {
        if (metric == ResourceMetric.Overview || string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        var sections = LitSSHmcp.Core.Services.Snapshot.Collectors.ResourceSnapshotCollector.SplitSections(raw);
        var keys = metric switch
        {
            ResourceMetric.Cpu => new[] { "meta", "stat1", "stat2", "load", "ps", "vmstat", "psi", "iostat" },
            ResourceMetric.Memory => new[] { "meta", "mem", "ps", "vmstat", "psi", "oom" },
            ResourceMetric.Disk => new[] { "meta", "df", "dfi", "mounts", "iostat", "journal", "docker", "du", "large", "deleted" },
            _ => Array.Empty<string>()
        };

        var builder = new StringBuilder();
        foreach (var key in keys)
        {
            if (sections.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                builder.AppendLine("##" + key);
                builder.AppendLine(value.TrimEnd());
            }
        }

        return builder.Length == 0 ? raw : builder.ToString();
    }
    public async Task<bool> SendPromptToAgentAsync(string prompt, bool sendImmediately)
    {
        if (_sendToAgent is null)
        {
            _dialogs.Warn("AI 运维助手未配置，无法发送分析请求。", "AI 分析");
            return false;
        }

        return await _sendToAgent(prompt, sendImmediately);
    }

    private void RefreshCollections(ResourceAnalysisReport report)
    {
        Replace(TopCpuProcesses, report.TopCpuProcesses);
        Replace(TopMemoryProcesses, report.TopMemoryProcesses);
        Replace(Disks, report.Disks);
        Replace(TopDirectories, report.TopDirectories);
        Replace(LargeFiles, report.LargeFiles);
        Replace(DeletedFiles, report.DeletedFiles);
        Replace(Findings, report.Findings.Where(f => f.Severity != ResourceSeverity.Ok));

        OnPropertyChanged(nameof(CurrentSummary));
        OnPropertyChanged(nameof(CurrentHeadline));
        OnPropertyChanged(nameof(CurrentDetail));
        OnPropertyChanged(nameof(CurrentSeverityText));
        OnPropertyChanged(nameof(OverviewHeadline));
        OnPropertyChanged(nameof(OverviewDetail));
        OnPropertyChanged(nameof(OverviewSeverityText));
        OnPropertyChanged(nameof(CpuHeadline));
        OnPropertyChanged(nameof(CpuDetail));
        OnPropertyChanged(nameof(CpuSeverityText));
        OnPropertyChanged(nameof(MemoryHeadline));
        OnPropertyChanged(nameof(MemoryDetail));
        OnPropertyChanged(nameof(MemorySeverityText));
        OnPropertyChanged(nameof(DiskHeadline));
        OnPropertyChanged(nameof(DiskDetail));
        OnPropertyChanged(nameof(DiskSeverityText));
        OnPropertyChanged(nameof(HasDeepData));
        OnPropertyChanged(nameof(HasDeletedFiles));
        OnPropertyChanged(nameof(HasDirectories));
        OnPropertyChanged(nameof(HasLargeFiles));
        OnPropertyChanged(nameof(JournalUsage));
        OnPropertyChanged(nameof(DockerUsage));
        OnPropertyChanged(nameof(OomText));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static void AppendProcessTable(StringBuilder builder, string title, IReadOnlyList<ResourceProcessItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"## {title}");
        foreach (var item in items.Take(15))
        {
            builder.AppendLine($"- PID {item.Pid} · {item.User}/{item.OwnerText} · CPU {item.CpuPercent:0.0}% · 内存 {item.MemPercent:0.0}% · RSS {item.RssText} · {item.Command}");
        }
    }

    private static string FormatMb(long mb)
        => mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb} MB";

    private static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars] + "\n…（已截断）";

    private void RaiseCommandStates()
    {
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeepScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
