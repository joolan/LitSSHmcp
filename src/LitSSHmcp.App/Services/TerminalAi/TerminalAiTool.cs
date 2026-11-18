using System.Text.Json;
using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.Services.TerminalAi;

public sealed record TerminalAiApprovalResult(bool Approved, bool ManualPassword);

public delegate Task<TerminalAiApprovalResult> TerminalAiApprovalHandler(
    string command, bool sudo, string workingDirectory, CommandFilterResult filter, CancellationToken ct);

/// <summary>
/// 暴露给终端 AI 的两个本地工具：terminal_exec / terminal_sudo。
/// 命令在当前 SSH 终端 PTY 中执行，复用命令过滤、人工审批与审计。
/// </summary>
public sealed class TerminalAiTool : IAgentTool
{
    public const string ExecToolName = "terminal_exec";
    public const string SudoToolName = "terminal_sudo";
    public const string ExecAuditToolName = "terminal_ai_exec";
    public const string SudoAuditToolName = "terminal_ai_sudo";

    private readonly TerminalSessionViewModel _terminal;
    private readonly TerminalCommandRunner _runner;
    private readonly ICommandFilterService _filter;
    private readonly IAuditLogService _audit;
    private readonly TerminalAiApprovalHandler _approval;
    private readonly Func<bool> _autoApprove;
    private readonly AITool _schema;

    public TerminalAiTool(
        TerminalSessionViewModel terminal,
        TerminalCommandRunner runner,
        ICommandFilterService filter,
        IAuditLogService audit,
        TerminalAiApprovalHandler approval,
        Func<bool> autoApprove,
        bool sudo)
    {
        _terminal = terminal;
        _runner = runner;
        _filter = filter;
        _audit = audit;
        _approval = approval;
        _autoApprove = autoApprove;
        IsSudo = sudo;
        Name = sudo ? SudoToolName : ExecToolName;
        _schema = AIFunctionFactory.Create(
            (string command, int timeoutSeconds = 60) => (command, timeoutSeconds),
            Name,
            Description);
    }

    public bool IsSudo { get; }
    public string Name { get; }
    public bool Destructive => IsSudo;
    public bool ReadOnly => false;
    public string? ToolGroup => "command";
    public bool IsLocal => true;
    public AITool Tool => _schema;

    private string Description => IsSudo
        ? "在当前 SSH 服务器终端中执行一条提权命令（按服务器配置使用 sudo/su）。共享当前终端 cwd/env/alias，始终需要用户确认；" +
          "只用于需要 root 权限的命令，普通只读命令请使用 terminal_exec。用户可在确认框选择在终端手动输入密码。返回退出码与输出。"
        : "在当前 SSH 服务器终端中执行一条命令（与用户当前终端共享 cwd/env/alias/后台任务）。" +
          "适用于会自行结束的诊断/变更命令；不要用于 vim/top/tail -f 等交互式或长驻命令；" +
          "不要在 terminal_exec 中运行 sudo/su，提权必须使用 terminal_sudo。" +
          "返回退出码、输出与耗时。危险命令仍可能要求人工确认。";

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var command = arguments is not null && arguments.TryGetValue("command", out var c)
            ? c?.ToString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(command))
            return Error("缺少 command 参数。");
        if (command.Contains('\0'))
            return Error("命令包含非法字符。");
        if (LooksLikeSessionExit(command))
            return Error("不允许通过 AI 退出或重连当前终端。");
        if (IsSudo && (command.Contains('\n') || command.Contains('\r')))
            return Error("terminal_sudo 只接受单行命令。请把多行脚本拆成多条命令逐条执行，避免密码输入被后续命令占用。");
        if (IsSudo && MentionsSudo(command))
            return Error("terminal_sudo 会自动提权，命令中不要再写 sudo/su；请直接提供要执行的命令。");
        if (!IsSudo && MentionsSudo(command))
            return Error("检测到 sudo/su：提权命令必须使用 terminal_sudo（会强制确认并支持手动输入密码），不要在 terminal_exec 中执行提权。");

        var filter = _filter.CheckCommand(command);
        if (filter == CommandFilterResult.Blocked)
        {
            await AuditAsync(command, CommandStatus.Blocked, "blocked", "安全策略拦截", null);
            return Error("命令被本机安全策略判定为危险，已拒绝执行。");
        }

        var requiresApproval = IsSudo || !_autoApprove() || filter != CommandFilterResult.Allowed;
        var manualPassword = false;
        string decision;
        if (requiresApproval)
        {
            var approval = await _approval(command, IsSudo, _terminal.Model.CurrentDirectory ?? string.Empty, filter, ct);
            if (!approval.Approved)
            {
                await AuditAsync(command, CommandStatus.Rejected, "manual-rejected", "用户拒绝", null);
                return Error("用户拒绝了该命令。");
            }
            manualPassword = IsSudo && approval.ManualPassword &&
                             _terminal.Server.SudoType != SudoType.None &&
                             !string.Equals(_terminal.Server.Username, "root", StringComparison.Ordinal);
            decision = "manual-approved";
        }
        else
        {
            decision = "auto-approved";
        }

        var timeout = arguments is not null && arguments.TryGetValue("timeoutSeconds", out var t) &&
                      int.TryParse(t?.ToString(), out var parsed)
            ? Math.Clamp(parsed, 1, 600)
            : 60;

        var result = await _runner.RunAsync(_terminal, command, IsSudo, TimeSpan.FromSeconds(timeout), ct,
            manualPassword);
        if (IsSudo && result.AuthFailed)
        {
            var fallback = await TryConfiguredSudoFallbackAsync(command, ct);
            if (fallback is not null && fallback.Success)
            {
                await AuditAsync(command, CommandStatus.Executed, "configured-sudo-fallback",
                    fallback.Output, fallback.ExitCode);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    executed = true,
                    executedVia = "configured-sudo-channel",
                    exitCode = fallback.ExitCode,
                    output = Truncate(fallback.Output, 30000),
                    escalation = fallback.Escalation,
                    note = "PTY sudo 认证失败，已回退到服务器配置的提权通道执行；该通道不共享当前终端的 cwd/env/alias。"
                });
            }

            var fallbackError = fallback?.Error ?? "配置提权通道不可用";
            await AuditAsync(command, CommandStatus.Failed, "manual-required",
                result.Output + "\n" + fallbackError, result.ExitCode);
            return JsonSerializer.Serialize(new
            {
                ok = false,
                executed = false,
                manualRequired = true,
                command,
                exitCode = result.ExitCode,
                output = Truncate(result.Output, 12000),
                error = result.Error + "；配置提权通道也失败：" + fallbackError,
                message = "可在确认框选择「在终端手动输入密码」，或点击步骤上的「填入终端」手动执行。"
            });
        }

        var status = result.Success ? CommandStatus.Executed : CommandStatus.Failed;
        await AuditAsync(command, status, decision, result.Output, result.ExitCode);

        return JsonSerializer.Serialize(new
        {
            ok = result.Success,
            executed = true,
            executedVia = "pty",
            exitCode = result.ExitCode,
            output = Truncate(result.Output, 30000),
            durationMs = (long)result.Duration.TotalMilliseconds,
            timedOut = result.TimedOut,
            busy = result.Busy,
            escalation = IsSudo
                ? (_terminal.Server.SudoType == SudoType.None ? "none" : _terminal.Server.SudoType.ToString())
                : "none",
            error = result.Error
        });
    }

    private async Task<CommandResult?> TryConfiguredSudoFallbackAsync(string command, CancellationToken ct)
    {
        try
        {
            var cwd = _terminal.Model.CurrentDirectory;
            var fallbackCommand = string.IsNullOrWhiteSpace(cwd)
                ? command
                : "cd " + Quote(cwd) + " && " + command;
            return await _terminal.SshService.ExecuteWithSudoAsync(_terminal.Server, fallbackCommand, ct);
        }
        catch
        {
            return null;
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static bool MentionsSudo(string command)
    {
        foreach (var token in command.Split(new[] { ' ', '\t', ';', '&', '|', '(', ')', '`' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, "sudo", StringComparison.Ordinal) ||
                string.Equals(token, "su", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool LooksLikeSessionExit(string command)
    {
        var trimmed = command.TrimStart();
        return trimmed.StartsWith("exit", StringComparison.Ordinal) &&
               (trimmed.Length == 4 || char.IsWhiteSpace(trimmed[4]) || trimmed[4] == ';') ||
               trimmed.StartsWith("logout", StringComparison.Ordinal);
    }

    private async Task AuditAsync(string command, CommandStatus status, string decision, string? result, int? exitCode)
    {
        try
        {
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = _terminal.Server.Id,
                ServerName = _terminal.Server.Name,
                Command = command,
                Result = Truncate(result, 4000),
                Status = status,
                Category = AuditCategory.Exec,
                Decision = decision,
                ExitCode = exitCode,
                Timestamp = DateTime.UtcNow,
                Tool = IsSudo ? SudoAuditToolName : ExecAuditToolName
            });
        }
        catch
        {
            // 审计失败不阻断终端命令，但调用方仍能看到命令结果。
        }
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { ok = false, error = message });

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text ?? string.Empty;
        return text[..max] + "\n…[输出已截断]";
    }
}
