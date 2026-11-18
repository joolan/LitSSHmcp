using System.Text.Json;
using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.Services.TerminalAi;

/// <summary>当前终端服务器上的后台长任务：start/status/log/cancel/list。</summary>
public sealed class TerminalJobTool : IAgentTool
{
    public const string ToolName = "terminal_job";

    private readonly TerminalSessionViewModel _terminal;
    private readonly TerminalCommandRunner _runner;
    private readonly ICommandFilterService _filter;
    private readonly IAuditLogService _audit;
    private readonly TerminalAiApprovalHandler _approval;
    private readonly Func<bool> _autoApprove;
    private readonly AITool _schema;

    public TerminalJobTool(
        TerminalSessionViewModel terminal,
        TerminalCommandRunner runner,
        ICommandFilterService filter,
        IAuditLogService audit,
        TerminalAiApprovalHandler approval,
        Func<bool> autoApprove)
    {
        _terminal = terminal;
        _runner = runner;
        _filter = filter;
        _audit = audit;
        _approval = approval;
        _autoApprove = autoApprove;
        _schema = AIFunctionFactory.Create(
            (string action, string? command = null, string? jobId = null, int tailLines = 200) =>
                (action, command, jobId, tailLines),
            ToolName,
            Description);
    }

    public string Name => ToolName;
    public bool Destructive => false;
    public bool ReadOnly => false;
    public string? ToolGroup => "command";
    public bool IsLocal => true;
    public AITool Tool => _schema;

    private const string Description =
        "当前 SSH 服务器终端上的后台长任务。action=start 启动后台命令并返回 jobId（不阻塞 AI）；" +
        "action=status 查看运行状态和最近日志；action=log 读取日志；action=cancel 取消；action=list 列出任务。" +
        "用于构建/下载/长脚本等长时间任务；不要用于 vim/top/tail -f 等交互式命令。命令必须单行，且不在命令内使用 sudo/su。";

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var action = Arg(arguments, "action")?.Trim().ToLowerInvariant() ?? string.Empty;
        return action switch
        {
            "start" => await StartAsync(arguments, ct),
            "status" => await StatusAsync(arguments, ct),
            "log" => await LogAsync(arguments, ct),
            "cancel" => await CancelAsync(arguments, ct),
            "list" => await ListAsync(ct),
            _ => Error("action 必须是 start/status/log/cancel/list。")
        };
    }

    private async Task<object?> StartAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var command = Arg(arguments, "command")?.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return Error("action=start 时缺少 command。");
        if (command.Contains('\n') || command.Contains('\r'))
            return Error("terminal_job 只接受单行命令；多行脚本请写成单行或上传为脚本文件后执行。");
        if (MentionsSudo(command))
            return Error("后台任务命令中不要包含 sudo/su；需要提权的短命令请使用 terminal_sudo。");

        var filter = _filter.CheckCommand(command);
        if (filter == CommandFilterResult.Blocked)
        {
            await AuditAsync("start", command, CommandStatus.Blocked, "blocked", null);
            return Error("命令被安全策略判定为危险，已拒绝。");
        }
        if (!_autoApprove() || filter != CommandFilterResult.Allowed)
        {
            var approval = await _approval(command, false, _terminal.Model.CurrentDirectory ?? string.Empty, filter, ct);
            if (!approval.Approved)
            {
                await AuditAsync("start", command, CommandStatus.Rejected, "manual-rejected", null);
                return Error("用户拒绝了后台任务。");
            }
        }

        var jobId = "j-" + Guid.NewGuid().ToString("N")[..8];
        var escaped = Quote(command);
        var start = "mkdir -p ~/.litssh-ai/jobs; " +
                    $"log=~/.litssh-ai/jobs/{jobId}.log; " +
                    $"nohup setsid /bin/sh -c {escaped} > \"$log\" 2>&1 & " +
                    $"echo $! > ~/.litssh-ai/jobs/{jobId}.pid; " +
                    $"printf '%s\\n' {escaped} > ~/.litssh-ai/jobs/{jobId}.cmd; " +
                    $"echo JOB_STARTED:{jobId}:$(cat ~/.litssh-ai/jobs/{jobId}.pid):$log";

        var result = await _runner.RunAsync(_terminal, start, sudo: false, TimeSpan.FromSeconds(20), ct);
        await AuditAsync("start", command, result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            result.Success ? "started" : "failed", result.ExitCode);
        if (!result.Output.Contains("JOB_STARTED:", StringComparison.Ordinal))
            return Error("后台任务启动失败：" + Truncate(result.Output + result.Error, 2000));

        var pid = ReadStartedPid(result.Output);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            jobId,
            pid,
            logPath = $"~/.litssh-ai/jobs/{jobId}.log",
            command,
            message = "后台任务已启动。使用 terminal_job(action=status|log|cancel, jobId=...) 查看或取消。"
        });
    }

    private async Task<object?> StatusAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var jobId = RequireJobId(arguments);
        if (jobId is null)
            return Error("缺少 jobId。");
        var script = $"j={Quote(jobId)}; pid=$(cat ~/.litssh-ai/jobs/$j.pid 2>/dev/null); " +
                     "if [ -n \"$pid\" ] && kill -0 \"$pid\" 2>/dev/null; then echo STATUS:RUNNING; " +
                     "else echo STATUS:STOPPED; fi; " +
                     "ps -p \"$pid\" -o pid=,stat=,etime=,cmd= 2>/dev/null; " +
                     "echo ---LOG---; tail -n 40 ~/.litssh-ai/jobs/$j.log 2>/dev/null";
        var result = await _runner.RunAsync(_terminal, script, sudo: false, TimeSpan.FromSeconds(15), ct);
        return JsonSerializer.Serialize(new
        {
            ok = result.Success,
            jobId,
            output = Truncate(result.Output, 12000),
            error = result.Error
        });
    }

    private async Task<object?> LogAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var jobId = RequireJobId(arguments);
        if (jobId is null)
            return Error("缺少 jobId。");
        var lines = Math.Clamp(ParseInt(Arg(arguments, "tailLines"), 200), 1, 2000);
        var result = await _runner.RunAsync(_terminal,
            $"tail -n {lines} ~/.litssh-ai/jobs/{jobId}.log 2>/dev/null",
            sudo: false, TimeSpan.FromSeconds(15), ct);
        return JsonSerializer.Serialize(new
        {
            ok = result.Success,
            jobId,
            output = Truncate(result.Output, 30000),
            error = result.Error
        });
    }

    private async Task<object?> CancelAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var jobId = RequireJobId(arguments);
        if (jobId is null)
            return Error("缺少 jobId。");
        var script = $"j={Quote(jobId)}; pid=$(cat ~/.litssh-ai/jobs/$j.pid 2>/dev/null); " +
                     "if [ -n \"$pid\" ]; then kill -TERM -\"$pid\" 2>/dev/null || kill -TERM \"$pid\" 2>/dev/null; " +
                     "sleep 1; kill -KILL -\"$pid\" 2>/dev/null || true; echo CANCELLED:$pid; " +
                     "else echo NOT_RUNNING; fi";
        var result = await _runner.RunAsync(_terminal, script, sudo: false, TimeSpan.FromSeconds(15), ct);
        await AuditAsync("cancel", jobId, result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            "cancel", result.ExitCode);
        return JsonSerializer.Serialize(new
        {
            ok = result.Success,
            jobId,
            output = Truncate(result.Output, 2000),
            error = result.Error
        });
    }

    private async Task<object?> ListAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync(_terminal,
            "ls -1t ~/.litssh-ai/jobs/*.pid 2>/dev/null | head -20 | sed 's#.*/##; s#\\.pid$##'",
            sudo: false, TimeSpan.FromSeconds(15), ct);
        return JsonSerializer.Serialize(new
        {
            ok = result.Success,
            output = Truncate(result.Output, 4000),
            error = result.Error
        });
    }

    private async Task AuditAsync(string action, string command, CommandStatus status, string decision, int? exitCode)
    {
        try
        {
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = _terminal.Server.Id,
                ServerName = _terminal.Server.Name,
                Command = action + ": " + command,
                Result = decision,
                Status = status,
                Category = AuditCategory.Exec,
                Decision = decision,
                ExitCode = exitCode,
                Timestamp = DateTime.UtcNow,
                Tool = "terminal_ai_job"
            });
        }
        catch
        {
            // ignore
        }
    }

    private static string? RequireJobId(IDictionary<string, object?>? arguments)
    {
        var value = Arg(arguments, "jobId")?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_')))
            return null;
        return value;
    }

    private static string? Arg(IDictionary<string, object?>? args, string name)
        => args is not null && args.TryGetValue(name, out var value) ? value?.ToString() : null;

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool MentionsSudo(string command)
        => command.Split(new[] { ' ', '\t', ';', '&', '|', '(', ')', '`' },
                StringSplitOptions.RemoveEmptyEntries)
            .Any(t => t is "sudo" or "su");

    private static string ReadStartedPid(string output)
    {
        var marker = output.Split('\n').FirstOrDefault(l => l.Contains("JOB_STARTED:", StringComparison.Ordinal));
        if (marker is null)
            return string.Empty;
        var parts = marker.Trim().Split(':');
        return parts.Length >= 3 ? parts[^2] : string.Empty;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Error(string message) => JsonSerializer.Serialize(new { ok = false, error = message });

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text ?? string.Empty;
        return text[..max] + "\n…[输出已截断]";
    }
}
