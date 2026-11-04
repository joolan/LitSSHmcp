using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

/// <summary>主页本机运行看板：CPU / 内存 / 磁盘 / 网络、趋势、健康评分与 TOP 进程。</summary>
public sealed class HomeDashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaxHistory = 450;
    private static readonly Color CpuColor = Color.FromRgb(0x4C, 0xC2, 0xFF);
    private static readonly Color MemoryColor = Color.FromRgb(0x6C, 0xCB, 0x5F);

    private readonly LocalSystemMetricsService _metrics = new();
    private readonly DispatcherTimer _timer;
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _memoryHistory = new();
    private readonly Queue<double> _networkHistory = new();

    private LocalSystemSnapshot? _snapshot;
    private bool _sampling;
    private bool _disposed;
    private bool _started;
    private bool _hasData;
    private bool _isPaused;

    private double _cpuPercent;
    private double _memoryPercent;
    private double _diskPercent;
    private double _networkInBps;
    private double _networkOutBps;
    private double _healthScore = 100;
    private int _processCount;

    private string _machineName = "本机";
    private string _osName = string.Empty;
    private string _cpuName = string.Empty;
    private string _lastUpdatedText = "正在读取本机指标…";
    private string _trendRangeMinutes = "1 分钟";
    private string _processSortMode = "CPU";

    private IReadOnlyList<double> _cpuHistoryView = Array.Empty<double>();
    private IReadOnlyList<double> _memoryHistoryView = Array.Empty<double>();
    private IReadOnlyList<double> _networkHistoryView = Array.Empty<double>();
    private IReadOnlyList<MetricSeries> _trendSeries = Array.Empty<MetricSeries>();

    public HomeDashboardViewModel()
    {
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        TogglePauseCommand = new RelayCommand(_ => IsPaused = !IsPaused);
        SetTrendRangeCommand = new RelayCommand(p => TrendRangeMinutes = p as string ?? "1 分钟");
        SetProcessSortCommand = new RelayCommand(p => ProcessSortMode = p as string ?? "CPU");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) =>
        {
            if (!IsPaused)
                _ = RefreshAsync();
        };
    }

    /// <summary>主页显示时开始采样（避免后台窗口长期占用性能计数器）。</summary>
    public void Start()
    {
        if (_disposed || _started)
            return;
        _started = true;
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>主页隐藏/视图卸载时停止采样。</summary>
    public void Stop()
    {
        _started = false;
        _timer.Stop();
    }

    public ICommand RefreshCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand SetTrendRangeCommand { get; }
    public ICommand SetProcessSortCommand { get; }


    public ObservableCollection<HomeProcessRow> Processes { get; } = new();

    public string MachineName => _machineName;
    public string MachineSummaryText => string.IsNullOrWhiteSpace(_osName)
        ? "正在读取本机信息…"
        : $"{_machineName} · {_osName}";
    public string CpuName => string.IsNullOrWhiteSpace(_cpuName) ? "正在读取 CPU 信息…" : _cpuName;

    public double CpuPercent => _hasData ? _cpuPercent : 0;
    public double MemoryPercent => _hasData ? _memoryPercent : 0;
    public double DiskPercent => _hasData ? _diskPercent : 0;
    public double CpuFraction => CpuPercent / 100.0;
    public double MemoryFraction => MemoryPercent / 100.0;
    public double DiskFraction => DiskPercent / 100.0;
    public double HealthPercent => _hasData ? _healthScore / 100.0 : 0;
    public int HealthScore => _hasData ? (int)Math.Round(_healthScore) : 0;
    public bool IsHealthGood => _hasData && _healthScore >= 85;
    public bool IsHealthWarning => _hasData && _healthScore >= 65 && _healthScore < 85;
    public bool IsHealthCritical => _hasData && _healthScore < 65;
    public string HealthText => !_hasData ? "读取中" : IsHealthGood ? "良好" : IsHealthWarning ? "注意" : "紧张";

    public string HealthReasonText
    {
        get
        {
            if (_snapshot is null)
                return "正在评估本机状态…";
            var reasons = new List<string>();
            if (MemoryPercent >= 70)
                reasons.Add($"内存 {LocalMetricFormatter.Percent(MemoryPercent)}");
            if (DiskPercent >= 75)
                reasons.Add($"磁盘 {LocalMetricFormatter.Percent(DiskPercent)}");
            if (CpuPercent >= 60)
                reasons.Add($"CPU {LocalMetricFormatter.Percent(CpuPercent)}");
            return reasons.Count == 0
                ? "各项指标正常"
                : "健康提示：" + string.Join(" · ", reasons.Take(2));
        }
    }

    public string HealthReasonLevel =>
        MemoryPercent >= 90 || DiskPercent >= 90 || CpuPercent >= 85 ? "critical"
        : MemoryPercent >= 70 || DiskPercent >= 75 || CpuPercent >= 60 ? "warn"
        : "ok";

    public string CpuStatusText => StatusText(CpuPercent, 60, 85);
    public string CpuStatusLevel => StatusLevel(CpuPercent, 60, 85);
    public string MemoryStatusText => StatusText(MemoryPercent, 70, 90);
    public string MemoryStatusLevel => StatusLevel(MemoryPercent, 70, 90);
    public string DiskStatusText => StatusText(DiskPercent, 75, 90);
    public string DiskStatusLevel => StatusLevel(DiskPercent, 75, 90);

    private static string StatusText(double value, double warning, double critical) =>
        value >= critical ? "偏高" : value >= warning ? "注意" : "正常";

    private static string StatusLevel(double value, double warning, double critical) =>
        value >= critical ? "critical" : value >= warning ? "warn" : "ok";

    public string CpuPercentText => LocalMetricFormatter.Percent(CpuPercent);
    public string MemoryPercentText => LocalMetricFormatter.Percent(MemoryPercent);
    public string DiskPercentText => LocalMetricFormatter.Percent(DiskPercent);
    public string CpuDetailText => _snapshot is null
        ? "正在采集"
        : $"{_snapshot.CoreCount} 核 · {_processCount} 个进程";
    public string MemoryDetailText
    {
        get
        {
            if (_snapshot is null)
                return "正在采集";
            var used = LocalMetricFormatter.Bytes(_snapshot.MemoryUsedBytes);
            var total = LocalMetricFormatter.Bytes(_snapshot.MemoryTotalBytes);
            return $"{used} / {total}";
        }
    }

    public string MemorySecondaryText
    {
        get
        {
            if (_snapshot is null)
                return string.Empty;
            return $"可用 {LocalMetricFormatter.Bytes(_snapshot.MemoryAvailableBytes)}";
        }
    }

    public string DiskDetailText
    {
        get
        {
            var disk = _snapshot?.SystemDisk;
            return disk is null
                ? "正在采集磁盘信息"
                : $"{disk.Name} · 可用 {LocalMetricFormatter.Bytes(disk.FreeBytes)}";
        }
    }

    public string DiskSummaryText
    {
        get
        {
            if (_snapshot is null || _snapshot.Disks.Count == 0)
                return "正在采集磁盘信息";
            return string.Join(" · ", _snapshot.Disks.Take(4)
                .Select(d => $"{d.Name} {LocalMetricFormatter.Percent(d.UsedPercent)}"));
        }
    }

    public string DiskIoText => _snapshot?.SystemDisk is { } disk
        ? $"{disk.Label} · {disk.Format}"
        : "磁盘容量与使用率";

    public string NetworkDownText => $"↓ {LocalMetricFormatter.Rate(_networkInBps)}";
    public string NetworkUpText => $"↑ {LocalMetricFormatter.Rate(_networkOutBps)}";
    public string NetworkDetailText => "所有活动网卡 · 实时吞吐";
    public string ProcessCountText => _snapshot is null ? "正在采集进程" : $"{_processCount} 个进程";
    public string UptimeText => _snapshot is null
        ? "正在读取运行时长"
        : $"已运行 {LocalMetricFormatter.Uptime(_snapshot.Uptime)}";
    public string LastUpdatedText => _lastUpdatedText;
    public string PauseText => IsPaused ? "继续" : "暂停";
    public string LiveStatusText => IsPaused ? "已暂停" : "实时 · 2 秒";
    public string LiveActionText => IsPaused ? "继续实时刷新" : "暂停实时刷新";

    public IReadOnlyList<double> CpuHistory
    {
        get => _cpuHistoryView;
        private set { _cpuHistoryView = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<double> MemoryHistory
    {
        get => _memoryHistoryView;
        private set { _memoryHistoryView = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<double> NetworkHistory
    {
        get => _networkHistoryView;
        private set { _networkHistoryView = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<MetricSeries> TrendSeries
    {
        get => _trendSeries;
        private set { _trendSeries = value; OnPropertyChanged(); }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused == value)
                return;
            _isPaused = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PauseText));
            OnPropertyChanged(nameof(LiveStatusText));
            OnPropertyChanged(nameof(LiveActionText));
            _lastUpdatedText = value ? "已暂停实时刷新" : "已继续实时刷新";
            OnPropertyChanged(nameof(LastUpdatedText));
        }
    }

    public string TrendRangeMinutes
    {
        get => _trendRangeMinutes;
        set
        {
            if (_trendRangeMinutes == value)
                return;
            _trendRangeMinutes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTrendRange1m));
            OnPropertyChanged(nameof(IsTrendRange5m));
            OnPropertyChanged(nameof(IsTrendRange15m));
            OnPropertyChanged(nameof(TrendStartLabel));
            RefreshTrendSeries();
        }
    }

    public bool IsTrendRange1m => _trendRangeMinutes == "1 分钟";
    public bool IsTrendRange5m => _trendRangeMinutes == "5 分钟";
    public bool IsTrendRange15m => _trendRangeMinutes == "15 分钟";

    public string TrendStartLabel => _trendRangeMinutes switch
    {
        "5 分钟" => "5 分钟前",
        "15 分钟" => "15 分钟前",
        _ => "1 分钟前"
    };

    public string ProcessSortMode
    {
        get => _processSortMode;
        set
        {
            if (_processSortMode == value)
                return;
            _processSortMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsProcessSortCpu));
            OnPropertyChanged(nameof(IsProcessSortMemory));
            UpdateProcesses();
        }
    }

    public bool IsProcessSortCpu => _processSortMode == "CPU";
    public bool IsProcessSortMemory => _processSortMode == "内存";

    public async Task RefreshAsync()
    {
        if (_sampling || _disposed)
            return;

        SetSampling(true);
        try
        {
            var snapshot = await Task.Run(_metrics.Sample);
            Apply(snapshot);
        }
        catch (Exception ex)
        {
            _lastUpdatedText = "读取本机指标失败：" + ex.Message;
            OnPropertyChanged(nameof(LastUpdatedText));
        }
        finally
        {
            SetSampling(false);
        }
    }

    private void SetSampling(bool value) => _sampling = value;

    private void Apply(LocalSystemSnapshot snapshot)
    {
        _snapshot = snapshot;
        _hasData = true;
        _cpuPercent = snapshot.CpuPercent;
        _memoryPercent = snapshot.MemoryPercent;
        _diskPercent = snapshot.SystemDisk?.UsedPercent ?? 0;
        _networkInBps = snapshot.NetworkInBytesPerSecond;
        _networkOutBps = snapshot.NetworkOutBytesPerSecond;
        _processCount = snapshot.ProcessCount;
        _machineName = snapshot.MachineName;
        _osName = snapshot.OsName;
        _cpuName = snapshot.CpuName;
        _healthScore = ComputeHealth(snapshot);
        _lastUpdatedText = $"更新于 {snapshot.Timestamp:HH:mm:ss} · 每 2 秒刷新";

        Enqueue(_cpuHistory, _cpuPercent);
        Enqueue(_memoryHistory, _memoryPercent);
        Enqueue(_networkHistory, _networkInBps + _networkOutBps);

        CpuHistory = Tail(_cpuHistory, 30);
        MemoryHistory = Tail(_memoryHistory, 30);
        NetworkHistory = Tail(_networkHistory, 30);
        RefreshTrendSeries();
        UpdateProcesses();

        NotifyDashboardChanged();
    }

    private static double ComputeHealth(LocalSystemSnapshot snapshot)
    {
        var cpu = snapshot.CpuPercent;
        var memory = snapshot.MemoryPercent;
        var disk = snapshot.SystemDisk?.UsedPercent ?? 0;
        var score = 100.0;
        score -= Math.Max(0, cpu - 60) * 1.1;
        score -= Math.Max(0, memory - 70) * 1.1;
        score -= Math.Max(0, disk - 75) * 1.1;

        // 任一关键指标进入高位时，整体至少降为「注意」，避免出现「健康良好 + 内存偏高」的语义冲突。
        if (cpu >= 90 || memory >= 90 || disk >= 90)
            score = Math.Min(score, 84);

        return Math.Clamp(score, 0, 100);
    }

    private static void Enqueue(Queue<double> queue, double value)
    {
        queue.Enqueue(double.IsFinite(value) ? Math.Max(0, value) : 0);
        while (queue.Count > MaxHistory)
            queue.Dequeue();
    }

    private static double[] Tail(Queue<double> queue, int count)
    {
        var skip = Math.Max(0, queue.Count - count);
        return queue.Skip(skip).ToArray();
    }

    /// <summary>趋势图固定窗口：历史不足时在左侧补 0，避免刚打开 5/15 分钟看起来像压缩了横轴。</summary>
    private static double[] TailPadded(Queue<double> queue, int count)
    {
        var result = new double[count];
        var available = Math.Min(queue.Count, count);
        if (available <= 0)
            return result;

        var values = queue.Skip(queue.Count - available).ToArray();
        Array.Copy(values, 0, result, count - available, available);
        return result;
    }

    private void RefreshTrendSeries()
    {
        var points = _trendRangeMinutes switch
        {
            "5 分钟" => 150,
            "15 分钟" => 450,
            _ => 30
        };

        var cpu = TailPadded(_cpuHistory, points);
        var memory = TailPadded(_memoryHistory, points);
        TrendSeries = new[]
        {
            new MetricSeries("CPU", CpuColor, cpu),
            new MetricSeries("内存", MemoryColor, memory)
        };
    }

    private void UpdateProcesses()
    {
        Processes.Clear();
        if (_snapshot is null)
            return;

        var byMemory = string.Equals(_processSortMode, "内存", StringComparison.Ordinal);
        var source = byMemory ? _snapshot.TopMemoryProcesses : _snapshot.TopCpuProcesses;
        var totalMemory = Math.Max(1, _snapshot.MemoryTotalBytes);
        for (var i = 0; i < source.Count; i++)
        {
            var process = source[i];
            var value = byMemory
                ? LocalMetricFormatter.Bytes(process.WorkingSetBytes)
                : LocalMetricFormatter.Percent(process.CpuPercent);
            var bar = byMemory
                ? Math.Clamp(process.WorkingSetBytes * 100.0 / totalMemory, 0, 100)
                : process.CpuPercent;
            Processes.Add(new HomeProcessRow(i + 1, process.Name, value, bar));
        }
    }

    private void NotifyDashboardChanged()
    {
        OnPropertyChanged(nameof(MachineName));
        OnPropertyChanged(nameof(MachineSummaryText));
        OnPropertyChanged(nameof(CpuName));
        OnPropertyChanged(nameof(CpuPercent));
        OnPropertyChanged(nameof(MemoryPercent));
        OnPropertyChanged(nameof(DiskPercent));
        OnPropertyChanged(nameof(CpuFraction));
        OnPropertyChanged(nameof(MemoryFraction));
        OnPropertyChanged(nameof(DiskFraction));
        OnPropertyChanged(nameof(HealthPercent));
        OnPropertyChanged(nameof(HealthScore));
        OnPropertyChanged(nameof(IsHealthGood));
        OnPropertyChanged(nameof(IsHealthWarning));
        OnPropertyChanged(nameof(IsHealthCritical));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HealthReasonText));
        OnPropertyChanged(nameof(HealthReasonLevel));
        OnPropertyChanged(nameof(CpuStatusText));
        OnPropertyChanged(nameof(CpuStatusLevel));
        OnPropertyChanged(nameof(MemoryStatusText));
        OnPropertyChanged(nameof(MemoryStatusLevel));
        OnPropertyChanged(nameof(DiskStatusText));
        OnPropertyChanged(nameof(DiskStatusLevel));
        OnPropertyChanged(nameof(CpuPercentText));
        OnPropertyChanged(nameof(MemoryPercentText));
        OnPropertyChanged(nameof(DiskPercentText));
        OnPropertyChanged(nameof(CpuDetailText));
        OnPropertyChanged(nameof(MemoryDetailText));
        OnPropertyChanged(nameof(MemorySecondaryText));
        OnPropertyChanged(nameof(DiskDetailText));
        OnPropertyChanged(nameof(DiskSummaryText));
        OnPropertyChanged(nameof(DiskIoText));
        OnPropertyChanged(nameof(NetworkDownText));
        OnPropertyChanged(nameof(NetworkUpText));
        OnPropertyChanged(nameof(NetworkDetailText));
        OnPropertyChanged(nameof(ProcessCountText));
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(LastUpdatedText));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _metrics.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>主页 TOP 进程行。</summary>
public sealed class HomeProcessRow
{
    public HomeProcessRow(int rank, string name, string valueText, double barPercent)
    {
        RankText = rank.ToString("00");
        Name = name;
        ValueText = valueText;
        BarPercent = Math.Clamp(barPercent, 0, 100);
    }

    public string RankText { get; }
    public string Name { get; }
    public string ValueText { get; }
    public double BarPercent { get; }
}
