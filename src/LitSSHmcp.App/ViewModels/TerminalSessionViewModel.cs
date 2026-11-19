using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.LocalTerminal;
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
    private readonly TerminalOutputBuffer _aiOutput = new(262144);
    private int _aiCommandRunning;
    private int _aiManualInput;

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
        if (IsAiCommandRunning)
            return "AI 正在使用该终端执行命令，请等待完成或点击停止。";
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

    /// <summary>供终端 AI 在 PTY 提权失败时回退到服务器配置的提权通道。</summary>
    public ISshService SshService => _ssh;

    /// <summary>该终端专属的 AI 助手（按终端隔离；随终端关闭释放）。</summary>
    public TerminalAiViewModel? AiAssistant { get; set; }

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
        _aiOutput.Append(text);
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

    /// <summary>AI 命令读取/执行期间，用户输入被锁定；只允许 Ctrl+C 中断。</summary>
    private static bool IsInterrupt(string text) => string.Equals(text, "\u0003", StringComparison.Ordinal);

    /// <summary>用户输入（键盘 / IME / 粘贴 / 发送框）：先写入本会话，再广播给其他已开启同步的会话。</summary>
    public void SendUserInput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var manual = IsAiAwaitingManualInput;
        if (IsAiCommandRunning && !manual && !IsInterrupt(text))
            return;
        SendDirectInput(text, logInput: !manual);
        if (!manual)
            _commandSync?.Broadcast(this, text);
    }

    /// <summary>命令同步目标写入：只写本会话，不再广播（避免递归）。</summary>
    public void SendReplicatedInput(string text)
    {
        if (IsAiAwaitingManualInput)
            return;
        if (IsAiCommandRunning && !IsInterrupt(text))
            return;
        SendDirectInput(text);
    }

    /// <summary>提权密码等敏感输入：不写会话日志/回放，仅写入 PTY。</summary>
    public void SendSensitiveInput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        lock (_gate)
            _shell?.Write(text);
    }

    public bool IsAiCommandRunning => Volatile.Read(ref _aiCommandRunning) != 0;

    /// <summary>AI 正在等待用户在终端手动输入 sudo/su 密码；仅此时允许用户输入，且不写日志。</summary>
    public bool IsAiAwaitingManualInput => Volatile.Read(ref _aiManualInput) != 0;

    public void BeginAiManualInput()
    {
        Interlocked.Exchange(ref _aiManualInput, 1);
        OnPropertyChanged(nameof(IsAiAwaitingManualInput));
    }

    public void EndAiManualInput()
    {
        Interlocked.Exchange(ref _aiManualInput, 0);
        OnPropertyChanged(nameof(IsAiAwaitingManualInput));
    }

    public bool TryBeginAiCommand()
    {
        if (Interlocked.CompareExchange(ref _aiCommandRunning, 1, 0) != 0)
            return false;
        OnPropertyChanged(nameof(IsAiCommandRunning));
        return true;
    }

    public void EndAiCommand()
    {
        Volatile.Write(ref _aiCommandRunning, 0);
        Volatile.Write(ref _aiManualInput, 0);
        OnPropertyChanged(nameof(IsAiCommandRunning));
        OnPropertyChanged(nameof(IsAiAwaitingManualInput));
    }

    public long OutputCursor => _aiOutput.Cursor;

    public TerminalOutputSnapshot ReadOutput(long cursor, int maxChars) => _aiOutput.Read(cursor, maxChars);

    public TerminalOutputSnapshot ReadRawOutput(long cursor, int maxChars) => _aiOutput.ReadRaw(cursor, maxChars);

    /// <summary>最近终端文本（已去除 ANSI/OSC），用于 AI 上下文预览。</summary>
    public string GetRecentTerminalText(int maxLines)
    {
        if (maxLines <= 0)
            return string.Empty;
        var snapshot = _aiOutput.Read(0, 262144);
        var text = TerminalOutputBuffer.Clean(snapshot.Text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(line =>
                !line.Contains("__litssh_", StringComparison.Ordinal) &&
                !line.Contains("<<<LITSSH_AI_", StringComparison.Ordinal) &&
                !line.TrimStart().StartsWith("stty -echo", StringComparison.Ordinal) &&
                !line.TrimStart().StartsWith("stty echo", StringComparison.Ordinal))
            .ToArray();
        if (lines.Length <= maxLines)
            return string.Join("\n", lines).TrimEnd();
        return string.Join("\n", lines.Skip(lines.Length - maxLines)).TrimEnd();
    }

    /// <summary>
    /// 当前任务相关的终端尾部：优先从最后一个 [AI] 标记开始截取，避免把无关历史
    /// 或固定 50 行的旧输出反复送进模型。
    /// </summary>
    public string GetTaskRelevantTerminalText(int maxLines = 40, int maxChars = 12000)
    {
        if (maxLines <= 0 || maxChars <= 0)
            return string.Empty;
        var text = GetRecentTerminalText(200);
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var start = 0;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Contains("[AI]", StringComparison.Ordinal))
            {
                start = i;
                break;
            }
        }

        var relevant = lines.Skip(start).ToArray();
        if (relevant.Length > maxLines)
            relevant = relevant.Skip(relevant.Length - maxLines).ToArray();
        var result = string.Join("\n", relevant).TrimEnd();
        if (result.Length <= maxChars)
            return result;
        return result[^maxChars..];
    }

    private void SendDirectInput(string text, bool logInput = true)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (logInput)
            _logger?.WriteInput(text);
        lock (_gate)
            _shell?.Write(text);
    }

    public void SendBytes(byte[] data)
    {
        if (data.Length == 0)
            return;
        var interrupt = data.Length == 1 && data[0] == 3;
        var manual = IsAiAwaitingManualInput;
        if (IsAiCommandRunning && !manual && !interrupt)
            return;
        if (!manual)
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
        Interlocked.Exchange(ref _aiCommandRunning, 0);
        Interlocked.Exchange(ref _aiManualInput, 0);
        var aiAssistant = AiAssistant;
        AiAssistant = null;
        if (aiAssistant is not null)
        {
            try { await aiAssistant.DisposeAsync(); } catch { /* ignore */ }
        }
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
