using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

/// <summary>一个已连接的服务器会话（对应主界面右侧一个标签页）。</summary>
public class SessionViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly IAuditLogService? _audit;

    private string _commandText = string.Empty;
    private string _output = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;

    public SessionViewModel(SshServerConfig server, ISshService ssh, IAuditLogService? audit)
    {
        Server = server;
        _ssh = ssh;
        _audit = audit;

        ExecuteCommand = new RelayCommand(_ => _ = ExecuteAsync(),
            _ => !IsBusy && !string.IsNullOrWhiteSpace(CommandText));
        ClearOutputCommand = new RelayCommand(_ => Output = string.Empty);
        TestConnectionCommand = new RelayCommand(_ => _ = TestAsync());

        _ = LoadHistoryAsync();
    }

    public SshServerConfig Server { get; }
    public string Title => Server.Name;
    public string Subtitle => $"{Server.Host}:{Server.Port}";
    public ObservableCollection<CommandAuditLog> RecentActivity { get; } = new();

    public string CommandText
    {
        get => _commandText;
        set { _commandText = value; OnPropertyChanged(); RaiseCanExecute(); }
    }

    public string Output
    {
        get => _output;
        set { _output = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); RaiseCanExecute(); }
    }

    public ICommand ExecuteCommand { get; }
    public ICommand ClearOutputCommand { get; }
    public ICommand TestConnectionCommand { get; }

    private void RaiseCanExecute() => (ExecuteCommand as RelayCommand)?.RaiseCanExecuteChanged();

    private async Task LoadHistoryAsync()
    {
        if (_audit == null) return;
        try
        {
            await _audit.InitializeAsync();
            var logs = await _audit.GetLogsAsync(Server.Id, 50);
            RecentActivity.Clear();
            foreach (var log in logs)
                RecentActivity.Add(log);
        }
        catch
        {
            // 历史加载失败不影响会话
        }
    }

    public async Task TestAsync()
    {
        if (Server.Disabled)
        {
            StatusMessage = "服务器已禁用, 已拒绝测试连接。";
            return;
        }

        StatusMessage = "测试连接中...";
        var ok = await _ssh.TestConnectionAsync(Server);
        StatusMessage = ok ? "连接成功" : "连接失败";
    }

    public async Task ExecuteAsync()
    {
        if (string.IsNullOrWhiteSpace(CommandText)) return;

        if (Server.Disabled)
        {
            StatusMessage = "服务器已禁用, 已拒绝执行命令。";
            return;
        }

        IsBusy = true;
        StatusMessage = "执行中...";
        try
        {
            var result = await _ssh.ExecuteCommandAsync(Server, CommandText);

            var piece = $"[{DateTime.Now:HH:mm:ss}] $ {CommandText}\n"
                        + (result.Success ? result.Output : $"[错误] {result.Error}")
                        + Environment.NewLine;
            Output = string.IsNullOrEmpty(Output) ? piece : Output + piece;

            StatusMessage = result.Success ? "命令执行完成" : "命令执行失败";

            RecentActivity.Insert(0, new CommandAuditLog
            {
                ServerId = Server.Id,
                ServerName = Server.Name,
                Command = CommandText,
                Result = result.Output,
                Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
                ExitCode = result.ExitCode,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            Output = (string.IsNullOrEmpty(Output) ? string.Empty : Output) + $"[错误] {ex.Message}{Environment.NewLine}";
            StatusMessage = "执行失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
