using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 本机 PowerShell 交互式终端标签（ConPTY）。既供用户直接操作，也供 AI 通过
/// <see cref="RunCommandAsync"/> 在**同一会话**里执行 CLI（共享 cwd/env，命令对用户可见）。
/// </summary>
public sealed class LocalTerminalSessionViewModel : INotifyPropertyChanged, IAsyncDisposable, ISshTab, ITerminalSession
{
    private readonly object _gate = new();
    private readonly TerminalOutputBuffer _output = new();
    private ConPtySession? _pty;
    private SessionLogger? _logger;
    private bool _started;
    private bool _disposed;
    private readonly bool _isAi;
    private readonly int _manualSeq;
    private string? _sessionName;

    public LocalTerminalSessionViewModel(bool isAi = false, int manualSeq = 1, int cols = 80, int rows = 24)
    {
        _isAi = isAi;
        _manualSeq = manualSeq;
        Model = new TerminalModel(cols, rows);
        ReconnectCommand = new RelayCommand(_ => _ = ReconnectAsync());
    }

    public TerminalModel Model { get; }

    public ICommand ReconnectCommand { get; }

    /// <summary>本机终端没有真实服务器：用合成配置让标签基建（重命名/浮动/关闭）复用。</summary>
    public SshServerConfig Server { get; } = new()
    {
        Id = "local-powershell",
        Name = "本机 PowerShell",
        Host = "localhost"
    };

    /// <summary>底层伪控制台进程 Id（0=未启动）；用于会话维度识别/存活性校验。</summary>
    public int ProcessId => _pty?.ProcessId ?? 0;

    public string Title => !string.IsNullOrWhiteSpace(_customTitle)
        ? _customTitle!
        : _isAi
            ? string.IsNullOrWhiteSpace(_sessionName)
                ? $"本机 PowerShell [AI-{ProcessId}]"
                : $"{_sessionName} [AI-{ProcessId}]"
            : _manualSeq <= 1 ? "本机 PowerShell" : $"本机 PowerShell ({_manualSeq})";

    public string? SessionName
    {
        get => _sessionName;
        set
        {
            if (string.Equals(_sessionName, value, StringComparison.Ordinal))
                return;
            _sessionName = value;
            OnPropertyChanged(nameof(Title));
        }
    }

    public bool IsRunning => _pty?.IsRunning == true;

    public long OutputCursor => _output.Cursor;

    public DateTimeOffset? LastOutputAt => _output.LastWriteAt;

    public TerminalOutputSnapshot ReadOutput(long cursor, int maxChars) => _output.Read(cursor, maxChars);

    public event Action? Exited;

    private string? _customTitle;

    public void Rename(string title)
    {
        _customTitle = title;
        OnPropertyChanged(nameof(Title));
    }

    public string Subtitle => "本机 · PowerShell";

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { _isConnected = value; OnPropertyChanged(); }
    }

    private string _statusMessage = "未启动";
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public async Task EnsureStartedAsync(int cols, int rows)
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            if (_started)
            {
                Model.Resize(cols, rows);
                _pty?.Resize((short)cols, (short)rows);
                return;
            }
            _started = true;
        }

        Model.Resize(cols, rows);
        await StartAsync(cols, rows);
    }

    private Task StartAsync(int cols, int rows)
    {
        StatusMessage = "启动中…";
        try
        {
            var pty = new ConPtySession();
            pty.Output += OnPtyOutput;
            pty.Exited += OnPtyExited;
            pty.Start((short)cols, (short)rows, LocalTerminalSettings.ShellPath, LocalTerminalSettings.WorkingDirectory);
            _pty = pty;
            if (TerminalSettings.RecordSessions)
                _logger = new SessionLogger("local-powershell", "localhost", Model.Cols, Model.Rows);
            IsConnected = true;
            StatusMessage = "已连接";
            OnPropertyChanged(nameof(Title));   // 标题含进程 Id，启动后刷新
        }
        catch (Exception ex)
        {
            StatusMessage = "启动失败: " + ex.Message;
            SafeFeed($"\r\n[启动失败] {ex.Message}\r\n");
        }
        return Task.CompletedTask;
    }

    public async Task ReconnectAsync()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            _started = false;
        }
        var old = _pty;
        _pty = null;
        if (old is not null)
        {
            old.Output -= OnPtyOutput;
            old.Exited -= OnPtyExited;
            old.Dispose();
        }
        try { _logger?.Dispose(); } catch { /* ignore */ }
        _logger = null;
        IsConnected = false;
        Model.Feed("\r\n[重新连接…]\r\n");
        lock (_gate)
        {
            _started = true;
        }
        await StartAsync(Model.Cols, Model.Rows);
    }

    /// <summary>在共享会话中执行命令并返回干净输出（供 AI 调用）。</summary>
    public Task<CommandResult> RunCommandAsync(string command, TimeSpan timeout, CancellationToken ct = default, bool captureOutput = true)
    {
        var pty = _pty;
        if (pty is null || !pty.IsRunning)
            throw new InvalidOperationException("本机终端未启动");
        return pty.RunCommandAsync(command, timeout, ct, captureOutput);
    }

    public void Interrupt() => _pty?.Write("\u0003");

    public void TerminateProcessTree() => _pty?.TerminateJobObject();

    public void SendInput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _logger?.WriteInput(text);
        _pty?.Write(text);
    }

    public void SendBytes(byte[] data)
    {
        if (data is { Length: > 0 })
        {
            try { _logger?.WriteInput(System.Text.Encoding.UTF8.GetString(data)); } catch { /* ignore */ }
        }
        _pty?.WriteBytes(data);
    }

    public void Resize(int cols, int rows)
    {
        Model.Resize(cols, rows);
        _pty?.Resize((short)cols, (short)rows);
    }

    private void OnPtyOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _output.Append(text);
        _logger?.Write(text);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            _ = dispatcher.BeginInvoke(new Action(() => SafeFeed(text)));
        else
            SafeFeed(text);
    }

    private void OnPtyExited()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            _ = dispatcher.BeginInvoke(new Action(ApplyExited));
        else
            ApplyExited();
    }

    private void ApplyExited()
    {
        IsConnected = false;
        if (!StatusMessage.StartsWith("启动失败", StringComparison.Ordinal))
            StatusMessage = "已退出";
        Exited?.Invoke();
    }

    private void SafeFeed(string text)
    {
        try { Model.Feed(text); }
        catch { /* 渲染异常不影响进程 */ }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        var pty = _pty;
        _pty = null;
        if (pty is not null)
        {
            pty.Output -= OnPtyOutput;
            pty.Exited -= OnPtyExited;
            pty.Dispose();
        }
        try { _logger?.Dispose(); } catch { /* ignore */ }
        _logger = null;
        return ValueTask.CompletedTask;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>本机终端偏好（可后续接入 AppConfig）。</summary>
public static class LocalTerminalSettings
{
    /// <summary>指定 shell 可执行文件；空=自动（优先 pwsh，回退 powershell）。</summary>
    public static string? ShellPath { get; set; }

    /// <summary>起始工作目录；空=用户主目录。</summary>
    public static string? WorkingDirectory { get; set; }
}
