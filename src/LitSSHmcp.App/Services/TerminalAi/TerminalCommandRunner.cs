using System.Diagnostics;
using System.Text;
using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services.TerminalAi;

public sealed record TerminalCommandRunResult(
    bool Success,
    int ExitCode,
    string Output,
    TimeSpan Duration,
    bool TimedOut,
    bool Busy,
    string? Error = null,
    bool AuthFailed = false);

/// <summary>
/// 在当前 SSH 终端 PTY 中注入命令，并用唯一标记关联输出与退出码。
/// 共享当前终端的 cwd/env/alias/后台任务；同一终端同时只允许一条 AI 命令。
/// </summary>
public sealed class TerminalCommandRunner
{
    private const string SudoPrompt = "LITSSH_SUDO_PASSWORD:";

    public async Task<TerminalCommandRunResult> RunAsync(
        TerminalSessionViewModel terminal,
        string command,
        bool sudo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        bool manualPassword = false)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Fail("命令为空。");
        if (!terminal.IsConnected)
            return Fail("终端未连接。");
        if (terminal.IsFullScreenActive)
            return Fail("终端正在运行全屏程序，AI 不能接管；请先退出或手动执行。");
        if (terminal.IsBusyOrUnsafe)
            return Fail("终端可能仍在执行命令或尚未回到提示符；请等待命令结束或手动执行。");
        if (!terminal.TryBeginAiCommand())
            return new TerminalCommandRunResult(false, -1, string.Empty, TimeSpan.Zero, false, true,
                "该终端已有 AI 命令在执行。");

        var stopwatch = Stopwatch.StartNew();
        var cursor = terminal.OutputCursor;
        var token = "LITSSH_AI_" + Guid.NewGuid().ToString("N");
        var marker = "<<<" + token + ">>>";
        var password = terminal.Server.SudoPassword ?? terminal.Server.Password ?? string.Empty;
        var isRoot = string.Equals(terminal.Server.Username, "root", StringComparison.Ordinal);
        var echoDisabled = true;   // AI 注入的包装行不进入终端显示；命令输出仍正常显示。
        var manualInput = sudo && manualPassword && !isRoot;
        var expectPassword = sudo && !manualInput && password.Length > 0 && !isRoot;
        var promptNeedle = terminal.Server.SudoType is SudoType.RootUser or SudoType.CustomUser
            ? "assword"
            : SudoPrompt;
        var passwordSent = false;
        var echoRestored = false;
        var effectiveTimeout = manualInput
            ? TimeSpan.FromSeconds(Math.Max(300, timeout.TotalSeconds))
            : timeout;

        try
        {
            // 第一步只发送短命令关闭回显，避免长行换行导致擦除不干净。
            terminal.SendInput(ToPtyInput("stty -echo 2>/dev/null || true"));
            await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);

            // 第二步擦除上一行并发出隐藏就绪标记；确认它已执行后再发正文。
            var echoReadyToken = "LITSSH_EO_" + Guid.NewGuid().ToString("N")[..16];
            terminal.SendInput(ToPtyInput(BuildEchoOffReady(echoReadyToken)));
            if (!await WaitForRawAsync(terminal, cursor, echoReadyToken, TimeSpan.FromSeconds(2),
                    cancellationToken).ConfigureAwait(false))
            {
                echoRestored = true;
                try { terminal.SendInput("stty echo\r"); } catch { /* ignore */ }
                return Fail("终端可能仍在执行上一条命令，AI 未发送新命令。请等待回到提示符后重试。");
            }

            var payload = BuildPayload(terminal.Server, command, sudo, token, echoDisabled, password,
                manualPassword);
            terminal.SendInput(ToPtyInput(payload));
            if (manualInput)
                terminal.BeginAiManualInput();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);
            var lastSnapshot = string.Empty;

            while (true)
            {
                await Task.Delay(60, CancellationToken.None).ConfigureAwait(false);
                var snapshot = terminal.ReadOutput(cursor, 524288);
                lastSnapshot = snapshot.Text;

                if (expectPassword && !passwordSent)
                {
                    var raw = terminal.ReadRawOutput(cursor, 524288).Text;
                    if (raw.Contains(promptNeedle, StringComparison.OrdinalIgnoreCase))
                    {
                        passwordSent = true;
                        terminal.SendSensitiveInput(password + "\r");
                    }
                }

                if (sudo && !manualInput && IsSudoAuthFailure(lastSnapshot))
                {
                    stopwatch.Stop();
                    try { terminal.SendInput("\u0003"); } catch { /* ignore */ }
                    await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                    if (echoDisabled)
                    {
                        echoRestored = true;
                        try { terminal.SendInput("stty echo\r"); } catch { /* ignore */ }
                    }
                    var output = StripWrapper(lastSnapshot, echoDisabled).TrimEnd();
                    return new TerminalCommandRunResult(false, -1, output, stopwatch.Elapsed,
                        false, false, "sudo 密码错误或认证失败，请检查服务器提权密码。", AuthFailed: true);
                }

                var markerIndex = lastSnapshot.LastIndexOf(marker, StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    stopwatch.Stop();
                    echoRestored = true;
                    var (exitCode, cwd) = ParseMarker(lastSnapshot, markerIndex + marker.Length);
                    var output = lastSnapshot[..markerIndex].TrimEnd();
                    output = StripWrapper(output, echoDisabled);
                    return new TerminalCommandRunResult(exitCode == 0, exitCode, output, stopwatch.Elapsed,
                        false, false);
                }

                if (timeoutCts.IsCancellationRequested)
                {
                    stopwatch.Stop();
                    try { terminal.SendInput("\u0003"); } catch { /* ignore */ }
                    await Task.Delay(600, CancellationToken.None).ConfigureAwait(false);
                    if (echoDisabled)
                    {
                        echoRestored = true;
                        try { terminal.SendInput("stty echo\r"); } catch { /* ignore */ }
                    }
                    var output = terminal.ReadOutput(cursor, 524288).Text;
                    output = StripWrapper(output, echoDisabled).TrimEnd();
                    return new TerminalCommandRunResult(false, -1, output, stopwatch.Elapsed,
                        true, false, cancellationToken.IsCancellationRequested ? "已停止。" : "命令执行超时。");
                }
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new TerminalCommandRunResult(false, -1, string.Empty, stopwatch.Elapsed, false, false,
                ex.Message);
        }
        finally
        {
            if (manualInput)
                terminal.EndAiManualInput();
            if (echoDisabled && !echoRestored)
            {
                try { terminal.SendInput("stty echo\r"); } catch { /* ignore */ }
            }
            terminal.EndAiCommand();
        }
    }

    private static TerminalCommandRunResult Fail(string message) =>
        new(false, -1, string.Empty, TimeSpan.Zero, false, false, message);

    /// <summary>回显关闭后发送：擦掉上一行，并用隐藏 OSC 标记确认已执行。</summary>
    internal static string BuildEchoOffReady(string readyToken) =>
        "printf '\\033[1A\\033[2K\\r\\033]0;" + readyToken + "\\007'";

    /// <summary>供「填入终端」使用的可手动执行提权命令：正常显示密码提示，由用户输入。</summary>
    internal static string BuildManualSudoCommand(SshServerConfig server, string command)
    {
        if (string.Equals(server.Username, "root", StringComparison.Ordinal))
            return command;
        var escaped = command.Replace("'", "'\\''", StringComparison.Ordinal);
        return server.SudoType switch
        {
            SudoType.RootUser => "su - root -c '" + escaped + "'",
            SudoType.CustomUser when !string.IsNullOrWhiteSpace(server.SudoUsername) =>
                "su - " + server.SudoUsername + " -c '" + escaped + "'",
            SudoType.None => command,
            _ => "sudo /bin/sh -c '" + escaped + "'"
        };
    }

    private static bool IsSudoAuthFailure(string text) =>
        text.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Authentication failure", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("a password is required", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> WaitForRawAsync(
        TerminalSessionViewModel terminal, long cursor, string token, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var raw = terminal.ReadRawOutput(cursor, 524288).Text;
            if (raw.Contains(token, StringComparison.Ordinal))
                return true;
            await Task.Delay(30, ct).ConfigureAwait(false);
        }
        return false;
    }

    internal static string BuildPayload(
        SshServerConfig server,
        string command,
        bool sudo,
        string token,
        bool disableEcho,
        string password,
        bool manualPassword = false)
    {
        var escaped = command.Replace("'", "'\\''", StringComparison.Ordinal);
        var runner = sudo ? BuildSudoCommand(server, escaped, password, manualPassword) : command;
        var sb = new StringBuilder();
        var needsHiddenPrompt = sudo && !manualPassword && password.Length > 0 &&
                                !string.Equals(server.Username, "root", StringComparison.Ordinal);
        if (needsHiddenPrompt)
        {
            // 用 OSC 设置标题作为密码提示：终端不显示文字，但又能在原始输出中检测到。
            sb.Append("__litssh_sudo_prompt=$(printf '\\033]0;").Append(SudoPrompt).Append("\\007')\n");
        }
        // 擦掉提示符行，然后把 AI 实际执行的命令显式写入终端/会话日志（回显已关闭）。
        if (manualPassword && sudo)
        {
            sb.Append("printf '\\r\\033[2K[AI] 需要 sudo 密码，请在终端输入（输入不回显）\\r\\n'\n");
        }
        var label = sudo ? "[AI][sudo] " : "[AI] ";
        sb.Append("printf '\\r\\033[2K").Append(label).Append("%s\\r\\n' '").Append(escaped).Append("'\n");
        if (sudo)
        {
            // sudo/su 会从 stdin 读取密码。完成标记必须和提权命令在同一行，
            // 否则后续行会被 sudo 当成密码输入，导致“未输入也 Sorry”并丢失标记。
            sb.Append(runner).Append("; __litssh_ec=$?; ");
            sb.Append("__litssh_m='<<<'").Append('"').Append(token).Append('"').Append("'>>>'; ");
            sb.Append("printf '\\n%s|%s|%s\\n\\033[1A\\033[2K\\r' \"$__litssh_m\" \"$__litssh_ec\" \"$PWD\"; ");
            if (disableEcho)
                sb.Append("stty echo 2>/dev/null || true");
            sb.Append('\n');
        }
        else
        {
            sb.Append(runner).Append('\n');
            sb.Append("__litssh_ec=$?\n");
            // 拼接标记，避免命令行回显提前命中。
            sb.Append("__litssh_m='<<<'").Append('"').Append(token).Append('"').Append("'>>>'\n");
            // 打印标记后立即用 ANSI 擦除该行：缓冲区仍能读到标记，但用户终端不残留。
            sb.Append("printf '\\n%s|%s|%s\\n\\033[1A\\033[2K\\r' \"$__litssh_m\" \"$__litssh_ec\" \"$PWD\"\n");
            if (disableEcho)
                sb.Append("stty echo 2>/dev/null || true\n");
        }
        return sb.ToString();
    }

    internal static string BuildSudoCommand(
        SshServerConfig server, string escapedCommand, string password, bool manualPassword = false)
    {
        if (string.Equals(server.Username, "root", StringComparison.Ordinal))
            return "/bin/sh -c '" + escapedCommand + "'";

        var hasPassword = password.Length > 0;
        var sudoArgs = manualPassword
            ? "-S -p 'Password: ' "
            : hasPassword
                ? "-S -p \"$__litssh_sudo_prompt\" "
                : "-n ";
        return server.SudoType switch
        {
            SudoType.None => "/bin/sh -c '" + escapedCommand + "'",
            SudoType.CurrentUser => "sudo " + sudoArgs + "/bin/sh -c '" + escapedCommand + "'",
            SudoType.Auto => "sudo " + sudoArgs + "/bin/sh -c '" + escapedCommand + "'",
            SudoType.RootUser => "su - root -c '" + escapedCommand + "'",
            SudoType.CustomUser when !string.IsNullOrWhiteSpace(server.SudoUsername) =>
                "su - " + server.SudoUsername + " -c '" + escapedCommand + "'",
            _ => "sudo " + sudoArgs + "/bin/sh -c '" + escapedCommand + "'"
        };
    }

    internal static string ToPtyInput(string payload)
    {
        var normalized = payload.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r", StringComparison.Ordinal);
        // 不要再追加一个空行：sudo -S 会把空行当成一次密码尝试。
        return normalized.EndsWith('\r') ? normalized : normalized + "\r";
    }

    internal static (int ExitCode, string Cwd) ParseMarker(string text, int markerEnd)
    {
        var rest = text[markerEnd..];
        var newline = rest.IndexOfAny(new[] { '\r', '\n' });
        if (newline >= 0)
            rest = rest[..newline];
        if (!rest.StartsWith('|'))
            return (-1, string.Empty);
        var second = rest.IndexOf('|', 1);
        if (second < 0)
            return (-1, string.Empty);
        var exitText = rest[1..second].Trim();
        var cwd = rest[(second + 1)..].Trim();
        return (int.TryParse(exitText, out var code) ? code : -1, cwd);
    }

    internal static string StripWrapper(string output, bool includeStty)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var filtered = lines.Where(line =>
            !line.Contains("__litssh_ec=", StringComparison.Ordinal) &&
            !line.Contains("__litssh_m=", StringComparison.Ordinal) &&
            !line.Contains("printf '\\n%s|%s|%s\\n", StringComparison.Ordinal) &&
            !line.TrimStart().StartsWith("[AI]", StringComparison.Ordinal) &&
            (!includeStty || !line.TrimStart().StartsWith("stty ", StringComparison.Ordinal)));
        return string.Join("\n", filtered).Trim('\n');
    }
}
