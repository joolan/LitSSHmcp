using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 性能诊断页：进程/GC、MCP 宿主池、最近 PerfLog 关键耗时。
/// 只读本机数据，不修改配置；用于用户反馈卡顿时快速提供上下文。
/// </summary>
public sealed class PerformanceDiagnosticsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public PerformanceDiagnosticsViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        CopyCommand = new RelayCommand(_ => CopySnapshot());
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
    }

    public ICommand RefreshCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    public ObservableCollection<DiagnosticMetric> Metrics { get; } = new();

    private string _summary = "正在读取…";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    private string _lastUpdatedText = string.Empty;
    public string LastUpdatedText { get => _lastUpdatedText; private set => Set(ref _lastUpdatedText, value); }

    private string _recentLog = string.Empty;
    public string RecentLog { get => _recentLog; private set => Set(ref _recentLog, value); }

    private string _logStatus = string.Empty;
    public string LogStatus { get => _logStatus; private set => Set(ref _logStatus, value); }

    public string LogPath => PerfLog.FilePath;

    public void Start()
    {
        if (_disposed)
            return;
        Refresh();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Refresh()
    {
        if (_disposed)
            return;

        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();

            var uptime = DateTime.Now - process.StartTime;
            Summary = $"PID {process.Id} · .NET {Environment.Version} · 已运行 {FormatDuration(uptime)} · {process.Threads.Count} 线程";

            var rows = new List<DiagnosticMetric>
            {
                new("工作集", FormatBytes(process.WorkingSet64), "Working Set"),
                new("私有内存", FormatBytes(process.PrivateMemorySize64), "Private Bytes"),
                new("提交内存", FormatBytes(process.PagedMemorySize64), "Paged Memory"),
                new("句柄 / 线程", $"{process.HandleCount} / {process.Threads.Count}", "Handle / Thread"),
                new("CPU 时间", $"{process.TotalProcessorTime.TotalSeconds:F1}s", "Total Processor Time"),
                new("GC 次数", $"{GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}", "Gen0 / Gen1 / Gen2"),
                new("托管堆", FormatBytes(GC.GetTotalMemory(forceFullCollection: false)), "GC.GetTotalMemory"),
                new("累计分配", FormatBytes(GC.GetTotalAllocatedBytes(precise: false)), "GC.GetTotalAllocatedBytes")
            };

            var pool = McpBuiltinHostPool.GetStats();
            rows.Add(new("MCP 宿主池", $"{pool.Total} 个", $"使用中 {pool.Active} · 空闲 {pool.Idle} · 连接中 {pool.Connecting}"));

            AppendPerfLog(rows);

            ReplaceMetrics(rows);
            LastUpdatedText = $"更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Summary = "诊断信息读取失败";
            LogStatus = ex.Message;
        }
    }

    private void AppendPerfLog(List<DiagnosticMetric> rows)
    {
        var path = PerfLog.FilePath;
        if (!File.Exists(path))
        {
            LogStatus = "未找到性能日志。设置环境变量 LITSSH_PERF=1 后重启 App 可记录。";
            RecentLog = string.Empty;
            return;
        }

        try
        {
            // 只保留最近 400 行，避免日志增长后诊断页本身变慢。
            var tail = new Queue<string>(400);
            foreach (var line in File.ReadLines(path))
            {
                tail.Enqueue(line);
                if (tail.Count > 400)
                    tail.Dequeue();
            }

            var lines = tail.ToArray();
            RecentLog = string.Join(Environment.NewLine, lines.TakeLast(60));

            var startup = ComputeStartup(lines);
            if (startup is not null)
                rows.Add(new("启动到主窗口", $"{startup.Value:F0} ms", "app.startup.begin → mainwindow.shown"));

            AddLatest(rows, lines, "mainwindow.ctor", "主窗口构造");
            AddLatest(rows, lines, "mainwindow.ctor.xaml", "主窗口 XAML");
            AddLatest(rows, lines, "mainwindow.create", "主窗口创建");
            AddLatest(rows, lines, "agent.view.xaml", "AI 页面 XAML");
            AddLatest(rows, lines, "agent.initialize", "AI 首开");
            AddLatest(rows, lines, "agent.runtime.start", "AI 运行时装配");
            AddLatest(rows, lines, "agent.runtime.assetgraph", "资产图谱提示词");
            AddLatest(rows, lines, "jenkins.refresh", "Jenkins 刷新");
            AddLatest(rows, lines, "kubernetes.refresh", "Kubernetes 刷新");

            LogStatus = $"已读取最近 {lines.Length} 行 · {path}";
        }
        catch (Exception ex)
        {
            LogStatus = "读取性能日志失败：" + ex.Message;
            RecentLog = string.Empty;
        }
    }

    private static double? ComputeStartup(string[] lines)
    {
        var beginIndex = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Contains("app.startup.begin", StringComparison.Ordinal))
            {
                beginIndex = i;
                break;
            }
        }
        if (beginIndex < 0)
            return null;

        for (var i = beginIndex + 1; i < lines.Length; i++)
        {
            if (!lines[i].Contains("mainwindow.shown", StringComparison.Ordinal))
                continue;
            if (TryParseTimestamp(lines[beginIndex], out var start) &&
                TryParseTimestamp(lines[i], out var end))
            {
                var ms = (end - start).TotalMilliseconds;
                if (ms < 0)
                    ms += TimeSpan.FromDays(1).TotalMilliseconds;
                return ms;
            }
        }
        return null;
    }

    private static void AddLatest(List<DiagnosticMetric> rows, string[] lines, string key, string name)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var index = lines[i].IndexOf(key + ":", StringComparison.Ordinal);
            if (index < 0)
                continue;

            var value = lines[i][(index + key.Length + 1)..].Trim();
            rows.Add(new DiagnosticMetric(name, value, key));
            return;
        }
    }

    private static bool TryParseTimestamp(string line, out DateTime value)
    {
        value = default;
        var token = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token is not null &&
               DateTime.TryParseExact(token, "HH:mm:ss.fff", null,
                   System.Globalization.DateTimeStyles.None, out value);
    }

    private void ReplaceMetrics(List<DiagnosticMetric> rows)
    {
        Metrics.Clear();
        foreach (var row in rows)
            Metrics.Add(row);
    }

    private void CopySnapshot()
    {
        try
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine("LitSSH 性能诊断");
            text.AppendLine(Summary);
            text.AppendLine(LastUpdatedText);
            text.AppendLine();
            foreach (var metric in Metrics)
                text.AppendLine($"{metric.Name}: {metric.Value}" +
                                (string.IsNullOrWhiteSpace(metric.Detail) ? string.Empty : $"  ({metric.Detail})"));
            text.AppendLine();
            text.AppendLine("最近性能日志：");
            text.AppendLine(RecentLog);
            Clipboard.SetText(text.ToString());
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(PerfLog.FilePath);
            if (string.IsNullOrWhiteSpace(dir))
                return;
            Directory.CreateDirectory(dir);
            NativeShell.OpenFolder(dir);
        }
        catch
        {
            // 打开资源管理器失败不影响诊断页
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };

    private static string FormatDuration(TimeSpan value) =>
        value.TotalDays >= 1
            ? $"{(int)value.TotalDays} 天 {value.Hours} 小时"
            : value.TotalHours >= 1
                ? $"{(int)value.TotalHours} 小时 {value.Minutes} 分"
                : $"{value.Minutes} 分 {value.Seconds} 秒";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
    }
}

public sealed record DiagnosticMetric(string Name, string Value, string Detail);
