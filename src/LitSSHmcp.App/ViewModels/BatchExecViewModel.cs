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

    public BatchExecViewModel(IEnumerable<SshServerConfig> servers, ISshService ssh)
    {
        _ssh = ssh;
        foreach (var server in servers)
            Servers.Add(new BatchServerItem(server));
        RunCommand = new RelayCommand(_ => _ = RunAsync(), _ => !IsRunning);
        SelectAllCommand = new RelayCommand(p => SetAll(p as string == "clear"));
    }

    public ObservableCollection<BatchServerItem> Servers { get; } = new();

    public ICommand RunCommand { get; }
    public ICommand SelectAllCommand { get; }

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
