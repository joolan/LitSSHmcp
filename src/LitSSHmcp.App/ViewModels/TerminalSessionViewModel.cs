using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

/// <summary>一个交互式 SSH 终端标签（PTY shell）。输出喂给 <see cref="TerminalModel"/>，键盘输入回写 shell。</summary>
public sealed class TerminalSessionViewModel : INotifyPropertyChanged, IAsyncDisposable, ISshTab, ITerminalSession, ICommandSyncParticipant
{
    private readonly ISshService _ssh;
    private readonly CommandSyncService? _commandSync;
    private readonly object _gate = new();
    private readonly object _outputGate = new();
    private readonly StringBuilder _pendingOutput = new();
    private int _flushScheduled;
    private long _generation;
    private int _reconnectAttempt;
    private CancellationTokenSource? _reconnectCts;
    private SshShellSession? _shell;
    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _disposed;
    private Services.SessionLogger? _logger;

    public TerminalSessionViewModel(SshServerConfig server, ISshService ssh, int cols = 80, int rows = 24, int ordinal = 1,
        CommandSyncService? commandSync = null)
    {
        Server = server;
        _ssh = ssh;
        _commandSync = commandSync;
        Ordinal = ordinal;
        Model = new TerminalModel(cols, rows);
        Model.AnsiPalette = TerminalSettings.Theme.Ansi16Palette;
        Model.ThemeFg = TerminalSettings.Theme.Fg;
        Model.ThemeBg = TerminalSettings.Theme.Bg;
        Model.ShellStateChanged += OnShellStateChanged;
        FileBrowser = new RemoteFileBrowserViewModel(server, ssh);
        FileBrowser.CdRequested = ChangeDirectory;
        FileBrowser.TerminalCommandSender = TrySendCommandToTerminal;
        ToggleFileBrowserCommand = new RelayCommand(_ => ShowFileBrowser = !ShowFileBrowser);
        ReconnectCommand = new RelayCommand(_ => _ = ReconnectAsync());
    }

    public ICommand ReconnectCommand { get; }

    /// <summary>在终端里 cd 到指定目录（用于文件列表右键「切换到此目录」）。</summary>
    public void ChangeDirectory(string remotePath) => SendInput($"cd {Quote(remotePath)}\r");

    /// <summary>
    /// 文件列表「终端操作」：仅在终端已连接且看起来回到 shell 提示符时发送命令。
    /// 返回 null 表示已发送；否则返回可读原因（由调用方提示用户）。
    /// </summary>
    public string? TrySendCommandToTerminal(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "命令为空。";
        if (_disposed)
            return "会话已关闭，无法发送命令。";
        if (!IsConnected)
            return "终端未连接，无法发送命令。请先连接或点击「重新连接」。";
        if (IsFullScreenActive)
            return "终端正在运行全屏程序（如 vim / top / less），请先退出后再试。";
        if (!IsLikelyIdle)
        {
            var line = Model.LastNonEmptyLineText().Trim();
            if (line.Length > 80)
                line = line[..80] + "…";
            return "终端可能仍在执行命令，请等待回到提示符后再试。\n当前最后一行：" + line;
        }

        SendInput(command + "\r");
        return null;
    }

    /// <summary>重开会话（用于 exit/logout 或断线后恢复）。</summary>
    public Task ReconnectAsync() => ReconnectCoreAsync(manual: true);

    private async Task ReconnectCoreAsync(bool manual)
    {
        if (_disposed)
            return;

        CancelReconnect();
        SshShellSession? old;
        CancellationTokenSource? oldCts;
        lock (_gate)
        {
            old = _shell;
            _shell = null;
            _started = false;
            _generation++;
            oldCts = _cts;
            _cts = null;
        }
        if (manual)
            _reconnectAttempt = 0;
        try { oldCts?.Cancel(); } catch { /* ignore */ }
        if (old is not null)
        {
            try { await old.DisposeAsync(); } catch { /* ignore */ }
        }
        oldCts?.Dispose();
        try { _logger?.Dispose(); } catch { /* ignore */ }
        _logger = null;
        IsConnected = false;
        if (manual)
            Model.Feed("\r\n[重新连接…]\r\n");
        await EnsureStartedAsync(Model.Cols, Model.Rows);
    }

    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    public SshServerConfig Server { get; }

    /// <summary>同一服务器下的终端序号（1 起）；用于区分标题与独立窗口布局。</summary>
    public int Ordinal { get; }

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

    public string Title => !string.IsNullOrWhiteSpace(_customTitle)
        ? _customTitle!
        : Ordinal <= 1 ? Server.Name : $"{Server.Name} ({Ordinal})";

    /// <summary>独立窗口布局记忆键：同服务器多终端需区分。</summary>
    public string LayoutKey => Ordinal <= 1 ? $"{Server.Id}-term" : $"{Server.Id}-term-{Ordinal}";

    private string? _customTitle;

    public void Rename(string title)
    {
        _customTitle = title;
        OnPropertyChanged(nameof(Title));
    }

    public string Subtitle => Model.CurrentDirectory is { Length: > 0 } cwd
        ? $"{Server.Host}:{Server.Port} · {cwd}"
        : $"{Server.Host}:{Server.Port}";

    public bool IsCommandRunning => Model.IsCommandRunning;

    public int? LastExitCode => Model.LastExitCode;

    public bool HasFailedCommand => Model.LastExitCode is > 0;

    public string StatusToolTip => StatusMessage +
        (Model.CurrentDirectory is { Length: > 0 } cwd ? "\n目录: " + cwd : string.Empty) +
        (Model.LastExitCode is int code ? "\n上条命令退出码: " + code : string.Empty);

    /// <summary>是否处于全屏程序（vim/top 等备用屏）——批量执行时视为不安全。</summary>
    public bool IsFullScreenActive => Model.IsAltScreen;

    /// <summary>是否“忙碌/不安全”：全屏程序运行中，或末行不像已回到 shell 提示符（批量执行时跳过）。</summary>
    public bool IsBusyOrUnsafe => !IsConnected || IsFullScreenActive || !IsLikelyIdle;

    /// <summary>末行是否是常见的 shell 提示符结尾（$ # % &gt; ❯ 等）。</summary>
    private bool IsLikelyIdle
    {
        get
        {
            var line = Model.LastNonEmptyLineText();
            if (string.IsNullOrWhiteSpace(line))
                return false;
            var last = line.TrimEnd()[^1];
            return last is '$' or '#' or '%' or '>' or '❯';
        }
    }

    private string _statusMessage = "未连接";
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            _statusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusToolTip));
        }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { _isConnected = value; OnPropertyChanged(); }
    }

    private bool _isSyncEnabled;
    /// <summary>是否参与命令同步（输入广播）。默认关闭；本机 PowerShell 无此开关。</summary>
    public bool IsSyncEnabled
    {
        get => _isSyncEnabled;
        set
        {
            if (_isSyncEnabled == value)
                return;
            _isSyncEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>首次显示时按实际控件尺寸打开 shell；已启动则仅调整尺寸。</summary>
    public async Task EnsureStartedAsync(int cols, int rows)
    {
        long generation;
        lock (_gate)
        {
            if (_started)
            {
                Model.Resize(cols, rows);
                _shell?.Resize((uint)cols, (uint)rows);
                return;
            }
            _started = true;
            generation = ++_generation;
        }

        Model.Resize(cols, rows);
        StatusMessage = "连接中…";
        try
        {
            var shell = await _ssh.OpenShellAsync(Server, "xterm-256color", (uint)cols, (uint)rows);
            if (_disposed || generation != _generation)
            {
                await shell.DisposeAsync();
                return;
            }

            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_disposed || generation != _generation)
                {
                    _ = shell.DisposeAsync();
                    return;
                }
                _shell = shell;
                _cts = new CancellationTokenSource();
                cts = _cts;
            }

            _reconnectAttempt = 0;
            if (TerminalSettings.RecordSessions)
                _logger = new Services.SessionLogger(Server.Name, Server.Host, Model.Cols, Model.Rows);
            IsConnected = true;
            StatusMessage = "已连接";
            // 连接建立前可能已按控件尺寸调整过模型：补一次窗口尺寸同步
            shell.Resize((uint)Model.Cols, (uint)Model.Rows);
            _ = ReadLoopAsync(shell, cts.Token, generation);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _started = false;
                    _shell = null;
                }
            }
            StatusMessage = "连接失败: " + ex.Message;
            Model.Feed($"\r\n[连接失败] {ex.Message}\r\n");
            ScheduleAutoReconnect(generation);
        }
    }

    private async Task ReadLoopAsync(SshShellSession shell, CancellationToken ct, long generation)
    {
        var buffer = new byte[8192];
        var charBuffer = new char[8192];
        var decoder = System.Text.Encoding.UTF8.GetDecoder();
        var unexpectedlyFailed = false;

        try
        {
            while (!ct.IsCancellationRequested && shell.IsConnected)
            {
                var n = await Task.Run(() => shell.Stream.Read(buffer, 0, buffer.Length), ct).ConfigureAwait(false);
                if (n <= 0)
                    break;

                decoder.Convert(buffer, 0, n, charBuffer, 0, charBuffer.Length, flush: false,
                    out _, out var charsUsed, out _);
                QueueOutput(new string(charBuffer, 0, charsUsed));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常关闭/重连
        }
        catch
        {
            unexpectedlyFailed = true;
        }

        if (_disposed || generation != _generation)
            return;

        OnDisconnected(unexpectedlyFailed, generation);
    }

    private void QueueOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _logger?.Write(text);
        lock (_outputGate)
        {
            _pendingOutput.Append(text);
            if (_flushScheduled != 0)
                return;
            _flushScheduled = 1;
        }
        _ = FlushOutputLoopAsync();
    }

    private async Task FlushOutputLoopAsync()
    {
        while (true)
        {
            await Task.Delay(33).ConfigureAwait(false);
            string text;
            lock (_outputGate)
            {
                if (_pendingOutput.Length == 0)
                {
                    _flushScheduled = 0;
                    return;
                }
                text = _pendingOutput.ToString();
                _pendingOutput.Clear();
            }

            if (_disposed)
                return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                _ = dispatcher.BeginInvoke(new Action(() => SafeFeed(text)));
            else
                SafeFeed(text);
        }
    }

    private void SafeFeed(string text)
    {
        try { Model.Feed(text); }
        catch { /* 渲染数据异常不应导致进程退出 */ }
    }

    /// <summary>连接结束：切回 UI 线程更新状态，并仅在当前连接代际上自动重连。</summary>
    private void OnDisconnected(bool unexpectedlyFailed, long generation)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            _ = dispatcher.BeginInvoke(new Action(() => ApplyDisconnected(unexpectedlyFailed, generation)));
        else
            ApplyDisconnected(unexpectedlyFailed, generation);
    }

    private void ApplyDisconnected(bool unexpectedlyFailed, long generation)
    {
        if (_disposed || generation != _generation)
            return;
        lock (_gate)
        {
            _started = false;
            _shell = null;
        }
        IsConnected = false;
        if (!StatusMessage.StartsWith("连接失败", StringComparison.Ordinal))
            StatusMessage = unexpectedlyFailed ? "连接异常，准备重连…" : "连接已断开";
        if (unexpectedlyFailed)
            ScheduleAutoReconnect(generation);
    }

    /// <summary>程序化下发（文件浏览器 cd、重连、AI 等）：不参与命令同步广播。</summary>
    private void ScheduleAutoReconnect(long generation)
    {
        if (_disposed || generation != _generation || _reconnectAttempt >= 5)
            return;
        CancelReconnect();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        var attempt = ++_reconnectAttempt;
        var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                if (_disposed || generation != _generation)
                    return;
                if (ReferenceEquals(_reconnectCts, cts))
                    _reconnectCts = null;
                cts.Dispose();
                await ReconnectCoreAsync(manual: false);
            }
            catch (OperationCanceledException)
            {
                // 手动重连/关闭会取消自动重连
            }
        });
    }

    private void CancelReconnect()
    {
        var cts = _reconnectCts;
        _reconnectCts = null;
        try { cts?.Cancel(); } catch { /* ignore */ }
        cts?.Dispose();
    }

    public void SendInput(string text) => SendDirectInput(text);

    /// <summary>用户输入（键盘 / IME / 粘贴 / 发送框）：先写入本会话，再广播给其他已开启同步的会话。</summary>
    public void SendUserInput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        SendDirectInput(text);
        _commandSync?.Broadcast(this, text);
    }

    /// <summary>命令同步目标写入：只写本会话，不再广播（避免递归）。</summary>
    public void SendReplicatedInput(string text) => SendDirectInput(text);

    private void SendDirectInput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _logger?.WriteInput(text);
        lock (_gate)
            _shell?.Write(text);
    }

    public void SendBytes(byte[] data)
    {
        if (data.Length > 0)
        {
            try { _logger?.WriteInput(System.Text.Encoding.UTF8.GetString(data)); } catch { /* ignore */ }
        }
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
        Model.ShellStateChanged -= OnShellStateChanged;
        CancelReconnect();
        try { _cts?.Cancel(); } catch { /* ignore */ }
        SshShellSession? shell;
        lock (_gate)
        {
            _generation++;
            shell = _shell;
            _shell = null;
        }
        lock (_outputGate)
            _pendingOutput.Clear();
        if (shell is not null)
            await shell.DisposeAsync();
        _cts?.Dispose();
        try { _logger?.Dispose(); } catch { /* ignore */ }
        _logger = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnShellStateChanged()
    {
        OnPropertyChanged(nameof(IsCommandRunning));
        OnPropertyChanged(nameof(LastExitCode));
        OnPropertyChanged(nameof(HasFailedCommand));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(StatusToolTip));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
