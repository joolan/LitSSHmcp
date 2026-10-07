using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.App.Controls;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

/// <summary>一个交互式 SSH 终端标签（PTY shell）。输出喂给 <see cref="TerminalModel"/>，键盘输入回写 shell。</summary>
public sealed class TerminalSessionViewModel : INotifyPropertyChanged, IAsyncDisposable, ISshTab
{
    private readonly ISshService _ssh;
    private readonly object _gate = new();
    private SshShellSession? _shell;
    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _disposed;
    private Services.SessionLogger? _logger;

    public TerminalSessionViewModel(SshServerConfig server, ISshService ssh, int cols = 80, int rows = 24)
    {
        Server = server;
        _ssh = ssh;
        Model = new TerminalModel(cols, rows);
        FileBrowser = new RemoteFileBrowserViewModel(server, ssh);
        FileBrowser.CdRequested = ChangeDirectory;
        ToggleFileBrowserCommand = new RelayCommand(_ => ShowFileBrowser = !ShowFileBrowser);
        ReconnectCommand = new RelayCommand(_ => _ = ReconnectAsync());
    }

    public ICommand ReconnectCommand { get; }

    /// <summary>在终端里 cd 到指定目录（用于文件列表右键「切换到此目录」）。</summary>
    public void ChangeDirectory(string remotePath) => SendInput($"cd {Quote(remotePath)}\r");

    /// <summary>重开会话（用于 exit/logout 或断线后恢复）。</summary>
    public async Task ReconnectAsync()
    {
        if (_disposed)
            return;

        SshShellSession? old;
        lock (_gate)
        {
            old = _shell;
            _shell = null;
            _started = false;
        }
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (old is not null)
        {
            try { await old.DisposeAsync(); } catch { /* ignore */ }
        }
        _cts?.Dispose();
        _cts = null;
        try { _logger?.Dispose(); } catch { /* ignore */ }
        _logger = null;
        IsConnected = false;
        Model.Feed("\r\n[重新连接…]\r\n");
        await EnsureStartedAsync(Model.Cols, Model.Rows);
    }

    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    public SshServerConfig Server { get; }

    public TerminalModel Model { get; }

    /// <summary>左侧远程文件浏览器（SFTP）。</summary>
    public RemoteFileBrowserViewModel FileBrowser { get; }

    public ICommand ToggleFileBrowserCommand { get; }

    private bool _showFileBrowser = TerminalSettings.ShowFileBrowser;
    public bool ShowFileBrowser
    {
        get => _showFileBrowser;
        set { _showFileBrowser = value; OnPropertyChanged(); }
    }

    public string Title => string.IsNullOrWhiteSpace(_customTitle) ? Server.Name : _customTitle!;

    private string? _customTitle;

    public void Rename(string title)
    {
        _customTitle = title;
        OnPropertyChanged(nameof(Title));
    }

    public string Subtitle => $"{Server.Host}:{Server.Port}";

    /// <summary>是否处于全屏程序（vim/top 等备用屏）——批量执行时视为不安全。</summary>
    public bool IsFullScreenActive => Model.IsAltScreen;

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
            if (_disposed)
            {
                await shell.DisposeAsync();
                return;
            }
            _shell = shell;
            if (TerminalSettings.RecordSessions)
                _logger = new Services.SessionLogger(Server.Name, Server.Host);
            IsConnected = true;
            StatusMessage = "已连接";
            // 连接建立前可能已按控件尺寸调整过模型：补一次窗口尺寸同步
            shell.Resize((uint)Model.Cols, (uint)Model.Rows);
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
        var charBuffer = new char[8192];
        var decoder = System.Text.Encoding.UTF8.GetDecoder();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        void FeedOnUi(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            _logger?.Write(text);
            if (dispatcher is not null)
                _ = dispatcher.BeginInvoke(new Action(() => SafeFeed(text)));
            else
                SafeFeed(text);
        }

        try
        {
            while (!ct.IsCancellationRequested && shell.IsConnected)
            {
                var n = await Task.Run(() => shell.Stream.Read(buffer, 0, buffer.Length), ct).ConfigureAwait(false);
                if (n <= 0)
                    break;

                decoder.Convert(buffer, 0, n, charBuffer, 0, charBuffer.Length, flush: false,
                    out _, out var charsUsed, out _);
                FeedOnUi(new string(charBuffer, 0, charsUsed));
            }
        }
        catch
        {
            // 连接结束/异常：落到下方状态
        }

        OnDisconnected();
    }

    private void SafeFeed(string text)
    {
        try { Model.Feed(text); }
        catch { /* 渲染数据异常不应导致进程退出 */ }
    }

    /// <summary>连接结束：切回 UI 线程更新状态（避免跨线程 PropertyChanged 引发异常）。</summary>
    private void OnDisconnected()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            _ = dispatcher.BeginInvoke(new Action(ApplyDisconnected));
        else
            ApplyDisconnected();
    }

    private void ApplyDisconnected()
    {
        IsConnected = false;
        if (!StatusMessage.StartsWith("连接失败", StringComparison.Ordinal))
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
        _disposed = true;
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
        try { _logger?.Dispose(); } catch { /* ignore */ }
        _logger = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
