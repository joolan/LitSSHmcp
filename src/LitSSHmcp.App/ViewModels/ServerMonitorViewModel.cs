using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

/// <summary>服务器资源监控小面板：定时采集 CPU/内存/磁盘/负载。</summary>
public sealed class ServerMonitorViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly DispatcherTimer _timer;
    private bool _busy;
    private long _prevIdle;
    private long _prevTotal;
    private bool _hasPrev;

    public ServerMonitorViewModel(SshServerConfig server, ISshService ssh)
    {
        Server = server;
        _ssh = ssh;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    public SshServerConfig Server { get; }
    public string Title => $"资源监控 - {Server.Name}";
    public string Subtitle => $"{Server.Host}:{Server.Port}";

    public void Start()
    {
        _ = RefreshAsync();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private int _cpu;
    public int CpuPercent { get => _cpu; private set { _cpu = value; OnPropertyChanged(); } }

    private int _mem;
    public int MemPercent { get => _mem; private set { _mem = value; OnPropertyChanged(); } }

    private string _memText = string.Empty;
    public string MemText { get => _memText; private set { _memText = value; OnPropertyChanged(); } }

    private int _disk;
    public int DiskPercent { get => _disk; private set { _disk = value; OnPropertyChanged(); } }

    private string _diskText = string.Empty;
    public string DiskText { get => _diskText; private set { _diskText = value; OnPropertyChanged(); } }

    private string _loadText = string.Empty;
    public string LoadText { get => _loadText; private set { _loadText = value; OnPropertyChanged(); } }

    private string _uptimeText = string.Empty;
    public string UptimeText { get => _uptimeText; private set { _uptimeText = value; OnPropertyChanged(); } }

    private string _statusMessage = "采集中…";
    public string StatusMessage { get => _statusMessage; private set { _statusMessage = value; OnPropertyChanged(); } }

    private const string Command =
        "echo ==LOAD==; cat /proc/loadavg; echo ==STAT==; head -1 /proc/stat; echo ==MEM==; free -m; " +
        "echo ==DISK==; df -P / | tail -1; echo ==UP==; cat /proc/uptime; echo ==CPU==; nproc";

    public async Task RefreshAsync()
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            var result = await _ssh.ExecuteCommandAsync(Server, Command, timeoutSeconds: 15);
            if (!result.Success)
            {
                StatusMessage = "采集失败: " + (string.IsNullOrEmpty(result.Error) ? result.Output : result.Error);
                return;
            }

            var sections = ParseSections(result.Output);
            UpdateCpu(GetLine(sections, "STAT", 0));
            UpdateLoad(GetLine(sections, "LOAD", 0));
            UpdateMem(sections.TryGetValue("MEM", out var mem) ? mem : new List<string>());
            UpdateDisk(GetLine(sections, "DISK", 0));
            UpdateUptime(GetLine(sections, "UP", 0));
            StatusMessage = $"更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusMessage = "采集失败: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private static Dictionary<string, List<string>> ParseSections(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("==") && line.EndsWith("=="))
            {
                current = line.Trim('=');
                sections[current] = new List<string>();
            }
            else if (current is not null)
            {
                sections[current].Add(line);
            }
        }
        return sections;
    }

    private static string? GetLine(Dictionary<string, List<string>> sections, string key, int index)
        => sections.TryGetValue(key, out var lines) && index < lines.Count ? lines[index] : null;

    private void UpdateCpu(string? statLine)
    {
        if (string.IsNullOrWhiteSpace(statLine))
            return;
        var parts = statLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return;
        var nums = new List<long>();
        for (var i = 1; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], out var v))
                break;
            nums.Add(v);
        }
        if (nums.Count < 4)
            return;

        var idle = nums[3] + (nums.Count > 4 ? nums[4] : 0);
        var total = nums.Sum();
        if (_hasPrev && total > _prevTotal)
        {
            var dTotal = total - _prevTotal;
            var dIdle = idle - _prevIdle;
            CpuPercent = (int)Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
        }
        _prevIdle = idle;
        _prevTotal = total;
        _hasPrev = true;
    }

    private void UpdateLoad(string? loadLine)
    {
        if (string.IsNullOrWhiteSpace(loadLine))
            return;
        var parts = loadLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        LoadText = parts.Length >= 3 ? $"负载 {parts[0]} / {parts[1]} / {parts[2]}" : loadLine;
    }

    private void UpdateMem(List<string> lines)
    {
        var mem = lines.FirstOrDefault(l => l.StartsWith("Mem:", StringComparison.Ordinal));
        if (mem is null)
            return;
        var parts = mem.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return;
        if (!long.TryParse(parts[1], out var total) || !long.TryParse(parts[2], out var used))
            return;
        MemPercent = total > 0 ? (int)(100.0 * used / total) : 0;
        MemText = $"{used} / {total} MB";
    }

    private void UpdateDisk(string? diskLine)
    {
        if (string.IsNullOrWhiteSpace(diskLine))
            return;
        var parts = diskLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return;
        // Filesystem 1024-blocks Used Available Capacity Mounted
        if (!long.TryParse(parts[1], out var total) || !long.TryParse(parts[2], out var used))
            return;
        DiskPercent = total > 0 ? (int)(100.0 * used / total) : 0;
        DiskText = $"{used / 1024} / {total / 1024} MB (/)";
    }

    private void UpdateUptime(string? upLine)
    {
        if (string.IsNullOrWhiteSpace(upLine))
            return;
        if (!double.TryParse(upLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var seconds))
            return;
        var ts = TimeSpan.FromSeconds(seconds);
        UptimeText = $"运行 {(int)ts.TotalDays} 天 {ts.Hours} 小时 {ts.Minutes} 分";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
