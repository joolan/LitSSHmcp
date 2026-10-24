using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

/// <summary>批量执行：在多台服务器上同时下发同一条命令并汇总输出。</summary>
public sealed class BatchExecViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;

    public BatchExecViewModel(IEnumerable<SshServerConfig> servers, ISshService ssh, IEnumerable<string>? selectedIds = null)
    {
        _ssh = ssh;
        var selected = selectedIds?.ToHashSet(StringComparer.Ordinal);
        foreach (var server in servers)
            Servers.Add(new BatchServerItem(server) { IsSelected = selected is null || selected.Contains(server.Id) });
        RunCommand = new RelayCommand(_ => _ = RunAsync(), _ => !IsRunning);
        SelectAllCommand = new RelayCommand(p => SetAll(p as string == "clear"));
        QuickActionCommand = new RelayCommand(p => { if (p is QuickAction a) _ = RunQuickAsync(a); }, _ => !IsRunning);
    }

    public ObservableCollection<BatchServerItem> Servers { get; } = new();

    public ICommand RunCommand { get; }
    public ICommand SelectAllCommand { get; }

    /// <summary>快速命令（免 AI 一键动作）：只读项直接执行，写操作（Dangerous）执行前需确认。</summary>
    public ICommand QuickActionCommand { get; }

    /// <summary>危险动作的确认回调（由窗口设置：返回 true=继续）。</summary>
    public Func<QuickAction, bool>? Confirm { get; set; }

    /// <summary>快捷命令 / 批量巡检动作列表。</summary>
    public IReadOnlyList<QuickAction> QuickActions { get; } = new[]
    {
        new QuickAction("一键巡检", "echo '== uptime =='; uptime; echo '== disk =='; df -h; echo '== mem =='; free -m; echo '== top cpu =='; ps -eo pid,pcpu,pmem,comm --sort=-pcpu | head -8", false),
        new QuickAction("磁盘 df -h", "df -h", false),
        new QuickAction("inode df -i", "df -i", false),
        new QuickAction("内存 free -m", "free -m", false),
        new QuickAction("占用端口", "ss -ltnp", false),
        new QuickAction("CPU Top", "ps -eo pid,ppid,pcpu,pmem,comm --sort=-pcpu | head -15", false),
        new QuickAction("内存 Top", "ps -eo pid,ppid,pcpu,pmem,comm --sort=-rss | head -15", false),
        new QuickAction("大目录 Top", "du -x -h --max-depth=1 / 2>/dev/null | sort -rh | head -15", false),
        new QuickAction("最近日志", "journalctl -n 100 --no-pager 2>/dev/null || tail -n 100 /var/log/messages 2>/dev/null", false),
        new QuickAction("Docker 容器", "docker ps --format 'table {{.Names}}\\t{{.Status}}\\t{{.Ports}}' 2>/dev/null || echo '(无 docker)'", false),
        new QuickAction("网络地址", "ip -brief addr 2>/dev/null || ifconfig -a", false),
        new QuickAction("⚠ 重启 nginx", "systemctl restart nginx", true),
        new QuickAction("⚠ 重启 docker", "systemctl restart docker", true),
    };

    private async Task RunQuickAsync(QuickAction action)
    {
        if (IsRunning)
            return;
        Command = action.Command;
        if (action.Dangerous)
        {
            var ok = Confirm?.Invoke(action) ?? false;
            if (!ok)
            {
                StatusMessage = $"已取消（未确认）：{action.Name}";
                return;
            }
        }
        await RunAsync();
    }

    private string _command = string.Empty;
    public string Command
    {
        get => _command;
        set { _command = value; OnPropertyChanged(); }
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            _isRunning = value;
            OnPropertyChanged();
            (RunCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void SetAll(bool selected)
    {
        foreach (var s in Servers)
            s.IsSelected = selected;
    }

    public async Task RunAsync()
    {
        if (IsRunning)
            return;
        var command = Command?.Trim();
        if (string.IsNullOrEmpty(command))
        {
            StatusMessage = "请输入命令";
            return;
        }

        var targets = Servers.Where(s => s.IsSelected).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "请勾选至少一台服务器";
            return;
        }

        IsRunning = true;
        StatusMessage = $"正在 {targets.Count} 台服务器上执行…";
        foreach (var t in targets)
        {
            t.Status = "运行中…";
            t.Output = string.Empty;
        }

        using var gate = new SemaphoreSlim(4);
        var tasks = targets.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                var result = await _ssh.ExecuteCommandAsync(item.Server, command, timeoutSeconds: 120);
                item.Status = result.Success ? $"成功 (exit {result.ExitCode})" : $"失败 (exit {result.ExitCode})";
                item.Output = string.IsNullOrEmpty(result.Output) ? result.Error : result.Output;
            }
            catch (Exception ex)
            {
                item.Status = "异常";
                item.Output = ex.Message;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        IsRunning = false;
        StatusMessage = $"完成：{targets.Count(s => s.Status.StartsWith("成功"))} 成功 / {targets.Count} 台";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>批量执行中的单台服务器项。</summary>
public sealed class BatchServerItem : INotifyPropertyChanged
{
    public BatchServerItem(SshServerConfig server) => Server = server;

    public SshServerConfig Server { get; }
    public string Name => Server.Name;
    public string Subtitle => $"{Server.Host}:{Server.Port}";

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    private string _output = string.Empty;
    public string Output
    {
        get => _output;
        set { _output = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一条快速命令 / 巡检动作。</summary>
public sealed record QuickAction(string Name, string Command, bool Dangerous);
