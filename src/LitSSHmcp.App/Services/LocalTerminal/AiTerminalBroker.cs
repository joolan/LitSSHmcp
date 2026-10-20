using System.Globalization;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Services.LocalTerminal;

public sealed class AiTerminalBroker : IAiTerminalBroker
{
    private const string StatusStarting = "启动中";
    private const string StatusRunning = "运行中";
    private const string StatusExited = "已退出";
    private const string StatusStopped = "已停止";
    private const string StatusTimedOut = "已超时";
    private const string StatusFailed = "失败";

    private readonly Func<string, LocalTerminalSessionViewModel> _createTerminal;
    private readonly Action<LocalTerminalSessionViewModel> _removeTerminal;
    private readonly Action<LocalTerminalSessionViewModel> _onOpened;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Entry>> _sessions = new(StringComparer.Ordinal);
    private int _nextId;

    public AiTerminalBroker(
        Func<string, LocalTerminalSessionViewModel> createTerminal,
        Action<LocalTerminalSessionViewModel> removeTerminal,
        Action<LocalTerminalSessionViewModel> onOpened)
    {
        _createTerminal = createTerminal;
        _removeTerminal = removeTerminal;
        _onOpened = onOpened;
    }

    public async Task<AiTerminalOpenResult> OpenAsync(
        string sessionKey,
        string name,
        string command,
        TimeSpan maxRuntime,
        int maxSessions,
        CancellationToken ct = default)
    {
        sessionKey = string.IsNullOrWhiteSpace(sessionKey) ? "default" : sessionKey.Trim();
        name = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(command))
            return new AiTerminalOpenResult(false, null, "缺少 command 参数。");

        var entry = new Entry(string.Empty, name, command.Trim(), DateTimeOffset.UtcNow);
        var evicted = new List<Entry>();
        string? limitError = null;

        lock (_gate)
        {
            var entries = GetEntries(sessionKey);
            var max = Math.Clamp(maxSessions, 1, 12);
            if (entries.Count >= max)
            {
                var need = entries.Count - max + 1;
                evicted = entries.Values
                    .Where(e => e.RunTask is { IsCompleted: true })
                    .OrderBy(e => e.StartedAt)
                    .Take(need)
                    .ToList();
                foreach (var old in evicted)
                    entries.Remove(old.Id);

                if (entries.Count >= max)
                {
                    var existing = string.Join(", ", entries.Values.Select(e => $"{e.Id}({e.Name})"));
                    limitError = $"本会话最多允许 {max} 个长驻终端（当前：{existing}）。请先用 local_shell_stop 停止不再需要的终端后再试。";
                }
            }

            if (limitError is null)
            {
                entry.Id = "term-" + (++_nextId).ToString(CultureInfo.InvariantCulture);
                entries[entry.Id] = entry;
            }
        }

        foreach (var old in evicted)
            DetachAndCancel(old, dispose: true);

        if (limitError is not null)
            return new AiTerminalOpenResult(false, null, limitError);

        try
        {
            var terminal = OnUi(() => _createTerminal(name));
            entry.Terminal = terminal;
            OnUiVoid(() => _onOpened(terminal));

            entry.ExitedHandler = () =>
            {
                entry.ProcessExited = true;
                if (entry.RunTask is { IsCompleted: false })
                {
                    try { entry.Cts?.Cancel(); } catch { /* ignore */ }
                }
            };
            terminal.Exited += entry.ExitedHandler;

            if (!terminal.IsRunning)
                await terminal.EnsureStartedAsync(terminal.Model.Cols, terminal.Model.Rows).ConfigureAwait(false);

            entry.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            entry.RunTask = terminal.RunCommandAsync(command, maxRuntime, entry.Cts.Token, captureOutput: false);
            entry.Status = entry.RunTask.IsCompleted ? StatusExited : StatusRunning;
            _ = ObserveAsync(entry);

            return new AiTerminalOpenResult(true, InfoOf(entry), null);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_sessions.TryGetValue(sessionKey, out var entries))
                    entries.Remove(entry.Id);
            }
            DetachAndCancel(entry, dispose: true);
            return new AiTerminalOpenResult(false, null, ex.Message);
        }
    }

    public IReadOnlyList<AiTerminalInfo> List(string sessionKey)
    {
        sessionKey = string.IsNullOrWhiteSpace(sessionKey) ? "default" : sessionKey.Trim();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionKey, out var entries))
                return Array.Empty<AiTerminalInfo>();
            return entries.Values.Select(InfoOf).ToList();
        }
    }

    public AiTerminalReadResult? Read(string sessionKey, string terminalId, long cursor, int maxChars)
    {
        sessionKey = string.IsNullOrWhiteSpace(sessionKey) ? "default" : sessionKey.Trim();
        Entry? entry;
        lock (_gate)
        {
            entry = FindEntry(sessionKey, terminalId);
        }
        if (entry?.Terminal is null)
            return null;

        var limit = maxChars <= 0 ? 8192 : Math.Clamp(maxChars, 256, 200_000);
        var snapshot = entry.Terminal.ReadOutput(Math.Max(0, cursor), limit);
        return new AiTerminalReadResult(snapshot.Text, snapshot.NextCursor, snapshot.Truncated, InfoOf(entry));
    }

    public async Task<AiTerminalRunResult> RunAsync(
        string sessionKey,
        string terminalId,
        string command,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        sessionKey = string.IsNullOrWhiteSpace(sessionKey) ? "default" : sessionKey.Trim();
        if (string.IsNullOrWhiteSpace(terminalId))
            return new AiTerminalRunResult(false, null, "缺少 terminalId。");
        if (string.IsNullOrWhiteSpace(command))
            return new AiTerminalRunResult(false, null, "缺少 command。");

        Entry? entry = null;
        CancellationTokenSource? cts = null;
        Task<CommandResult>? task = null;
        try
        {
            lock (_gate)
            {
                entry = FindEntry(sessionKey, terminalId);
                if (entry?.Terminal is null)
                    return new AiTerminalRunResult(false, null, $"未找到终端 {terminalId}。");
                if (entry.RunTask is { IsCompleted: false })
                    return new AiTerminalRunResult(false, null,
                        $"终端 {terminalId} 仍在执行上一条命令，请等它结束后再注入新命令。");
                if (!entry.Terminal.IsRunning)
                    return new AiTerminalRunResult(false, null,
                        $"终端 {terminalId} 的 PowerShell 进程已退出，无法再注入命令。");

                entry.StopRequested = false;
                entry.ProcessExited = false;
                entry.ExitCode = null;
                entry.Error = null;
                entry.Status = StatusRunning;
                cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                entry.Cts = cts;
                task = entry.Terminal.RunCommandAsync(command, timeout, cts.Token);
                entry.RunTask = task;
            }

            var result = await task.ConfigureAwait(false);
            lock (_gate)
            {
                entry.ExitCode = result.ExitCode;
                entry.Status = result.Busy
                    ? StatusRunning
                    : result.TimedOut || result.Killed
                        ? StatusTimedOut
                        : StatusExited;
            }
            return new AiTerminalRunResult(true, result, null);
        }
        catch (OperationCanceledException)
        {
            if (entry is not null)
            {
                lock (_gate)
                    entry.Status = entry.StopRequested ? StatusStopped : StatusExited;
            }
            return new AiTerminalRunResult(false, null, "命令已取消。");
        }
        catch (Exception ex)
        {
            if (entry is not null)
            {
                lock (_gate)
                {
                    entry.Status = StatusFailed;
                    entry.Error = ex.Message;
                }
            }
            return new AiTerminalRunResult(false, null, ex.Message);
        }
        finally
        {
            if (entry is not null && cts is not null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(entry.Cts, cts))
                        entry.Cts = null;
                }
            }
            cts?.Dispose();
        }
    }

    public async Task<AiTerminalStopResult> StopAsync(string sessionKey, string terminalId, CancellationToken ct = default)
    {
        sessionKey = string.IsNullOrWhiteSpace(sessionKey) ? "default" : sessionKey.Trim();
        Entry? entry;
        lock (_gate)
        {
            entry = FindEntry(sessionKey, terminalId);
        }
        if (entry is null)
            return new AiTerminalStopResult(false, "未找到", $"未找到终端 {terminalId}。");

        var wasRunning = entry.RunTask is { IsCompleted: false };
        if (wasRunning)
        {
            entry.StopRequested = true;
            try { entry.Cts?.Cancel(); } catch { /* ignore */ }
            try { entry.Terminal?.Interrupt(); } catch { /* ignore */ }

            if (entry.RunTask is { } task)
            {
                var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false);
                if (finished != task)
                {
                    try { entry.Terminal?.TerminateProcessTree(); } catch { /* ignore */ }
                    await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
                }
            }
            else
            {
                try { entry.Terminal?.TerminateProcessTree(); } catch { /* ignore */ }
            }

            if (entry.RunTask is null || !entry.RunTask.IsCompleted)
                entry.Status = StatusStopped;
        }

        var status = entry.Status;
        var closed = RemoveEntry(sessionKey, entry);
        var message = wasRunning
            ? "已发送停止信号，并关闭对应终端标签。"
            : "终端命令已经结束，已关闭对应终端标签。";
        if (!closed)
            message = wasRunning ? "已发送停止信号。" : "终端命令已经结束。";
        return new AiTerminalStopResult(true, status, message);
    }

    public void CloseTerminal(LocalTerminalSessionViewModel terminal)
    {
        Entry? entry = null;
        lock (_gate)
        {
            foreach (var entries in _sessions.Values)
            {
                entry = entries.Values.FirstOrDefault(e => ReferenceEquals(e.Terminal, terminal));
                if (entry is not null)
                {
                    entries.Remove(entry.Id);
                    break;
                }
            }
        }
        if (entry is not null)
            DetachAndCancel(entry, dispose: false);
    }

    private bool RemoveEntry(string sessionKey, Entry entry)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionKey, out var entries) ||
                !entries.TryGetValue(entry.Id, out var current) ||
                !ReferenceEquals(current, entry))
                return false;
            entries.Remove(entry.Id);
        }
        DetachAndCancel(entry, dispose: true);
        return true;
    }

    public void CloseSession(string sessionKey)
    {
        sessionKey = string.IsNullOrWhiteSpace(sessionKey) ? "default" : sessionKey.Trim();
        List<Entry> removed;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionKey, out var entries))
                return;
            removed = entries.Values.ToList();
        }
        foreach (var entry in removed)
            DetachAndCancel(entry, dispose: true);
    }

    public void CloseAll()
    {
        List<Entry> removed = new();
        lock (_gate)
        {
            foreach (var entries in _sessions.Values)
                removed.AddRange(entries.Values);
            _sessions.Clear();
        }
        foreach (var entry in removed)
            DetachAndCancel(entry, dispose: true);
    }

    private async Task ObserveAsync(Entry entry)
    {
        var task = entry.RunTask;
        if (task is null)
            return;
        try
        {
            var result = await task.ConfigureAwait(false);
            lock (_gate)
            {
                if (!ReferenceEquals(entry.RunTask, task))
                    return;
                entry.ExitCode = result.ExitCode;
                entry.Status = entry.StopRequested
                    ? StatusStopped
                    : entry.ProcessExited
                        ? StatusExited
                        : result.TimedOut
                            ? StatusTimedOut
                            : StatusExited;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(entry.RunTask, task))
                    entry.Status = entry.StopRequested ? StatusStopped : StatusExited;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(entry.RunTask, task))
                    return;
                entry.Status = StatusFailed;
                entry.Error = ex.Message;
            }
        }
    }

    private void DetachAndCancel(Entry entry, bool dispose)
    {
        try { entry.Cts?.Cancel(); } catch { /* ignore */ }
        if (entry.Terminal is { } terminal)
        {
            if (entry.ExitedHandler is not null)
            {
                try { terminal.Exited -= entry.ExitedHandler; } catch { /* ignore */ }
            }
            if (dispose)
                OnUiVoid(() => _removeTerminal(terminal));
        }
    }

    private Entry? FindEntry(string sessionKey, string terminalId)
    {
        if (!_sessions.TryGetValue(sessionKey, out var entries))
            return null;
        return entries.TryGetValue(terminalId, out var entry) ? entry : null;
    }

    private Dictionary<string, Entry> GetEntries(string sessionKey)
    {
        if (!_sessions.TryGetValue(sessionKey, out var entries))
        {
            entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            _sessions[sessionKey] = entries;
        }
        return entries;
    }

    private AiTerminalInfo InfoOf(Entry entry)
    {
        var terminal = entry.Terminal;
        var running = terminal is not null && terminal.IsRunning && entry.RunTask is { IsCompleted: false };
        return new AiTerminalInfo(
            entry.Id,
            entry.Name,
            entry.Status,
            terminal?.ProcessId ?? 0,
            running,
            entry.ExitCode,
            entry.Command,
            entry.StartedAt,
            terminal?.LastOutputAt);
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "终端";
        return trimmed.Length <= 40 ? trimmed : trimmed[..40];
    }

    private static T OnUi<T>(Func<T> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(action);
        return action();
    }

    private static void OnUiVoid(Action action)
        => OnUi(() => { action(); return true; });

    private sealed class Entry
    {
        public Entry(string id, string name, string command, DateTimeOffset startedAt)
        {
            Id = id;
            Name = name;
            Command = command;
            StartedAt = startedAt;
        }

        public string Id { get; set; }
        public string Name { get; }
        public string Command { get; }
        public DateTimeOffset StartedAt { get; }
        public LocalTerminalSessionViewModel? Terminal { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public Task<CommandResult>? RunTask { get; set; }
        public Action? ExitedHandler { get; set; }
        public string Status { get; set; } = StatusStarting;
        public int? ExitCode { get; set; }
        public string? Error { get; set; }
        public bool StopRequested { get; set; }
        public bool ProcessExited { get; set; }
    }
}
