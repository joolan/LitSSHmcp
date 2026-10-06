using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.App.Controls;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

/// <summary>一个交互式 SSH 终端标签（PTY shell）。输出喂给 <see cref="TerminalModel"/>，键盘输入回写 shell。</summary>
public sealed class TerminalSessionViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly ISshService _ssh;
    private readonly object _gate = new();
    private SshShellSession? _shell;
    private CancellationTokenSource? _cts;
    private bool _started;

    public TerminalSessionViewModel(SshServerConfig server, ISshService ssh, int cols = 80, int rows = 24)
    {
        Server = server;
        _ssh = ssh;
        Model = new TerminalModel(cols, rows);
    }

    public SshServerConfig Server { get; }

    public TerminalModel Model { get; }

    public string Title => Server.Name;

    public string Subtitle => $"{Server.Host}:{Server.Port}";

    private string _statusMessage = "未连接";
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { _isConnected = value; OnPropertyChanged(); }
    }

    /// <summary>首次显示时按实际控件尺寸打开 shell；已启动则仅调整尺寸。</summary>
    public async Task EnsureStartedAsync(int cols, int rows)
    {
        lock (_gate)
        {
            if (_started)
            {
                Model.Resize(cols, rows);
                _shell?.Resize((uint)cols, (uint)rows);
                return;
            }
            _started = true;
        }

        Model.Resize(cols, rows);
        StatusMessage = "连接中…";
        try
        {
            var shell = await _ssh.OpenShellAsync(Server, "xterm-256color", (uint)cols, (uint)rows);
            _shell = shell;
            IsConnected = true;
            StatusMessage = "已连接";
            _cts = new CancellationTokenSource();
            _ = ReadLoopAsync(shell, _cts.Token);
        }
        catch (Exception ex)
        {
            StatusMessage = "连接失败: " + ex.Message;
            Model.Feed($"\r\n[连接失败] {ex.Message}\r\n");
        }
    }

    private async Task ReadLoopAsync(SshShellSession shell, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        try
        {
            while (!ct.IsCancellationRequested && shell.IsConnected)
            {
                var n = await Task.Run(() => shell.Stream.Read(buffer, 0, buffer.Length), ct).ConfigureAwait(false);
                if (n <= 0)
                    break;

                var data = new byte[n];
                Array.Copy(buffer, data, n);
                if (dispatcher is not null)
                    _ = dispatcher.BeginInvoke(new Action(() => Model.Feed(data, 0, n)));
                else
                    Model.Feed(data, 0, n);
            }
        }
        catch
        {
            // 连接结束/异常：落到下方状态
        }

        IsConnected = false;
        if (!string.IsNullOrEmpty(StatusMessage) && StatusMessage != "连接失败: ")
            StatusMessage = "连接已断开";
    }

    public void SendInput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        lock (_gate)
            _shell?.Write(text);
    }

    public void SendBytes(byte[] data)
    {
        lock (_gate)
            _shell?.Write(data, 0, data.Length);
    }

    public void Resize(int cols, int rows)
    {
        Model.Resize(cols, rows);
        lock (_gate)
            _shell?.Resize((uint)cols, (uint)rows);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        SshShellSession? shell;
        lock (_gate)
        {
            shell = _shell;
            _shell = null;
        }
        if (shell is not null)
            await shell.DisposeAsync();
        _cts?.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
