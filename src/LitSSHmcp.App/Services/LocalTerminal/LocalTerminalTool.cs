using System.Linq;
using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.Services.LocalTerminal;

/// <summary>用户在审批对话框里的选择。</summary>
public enum LocalApprovalDecision
{
    Deny,
    Allow,
    AlwaysAllow,
    /// <summary>本轮次内允许全部命令（发送下一条用户消息后恢复审批）。</summary>
    AllowRound,
    /// <summary>本次 AI 会话期间允许全部命令（不建议；重启/新会话即失效）。</summary>
    AllowSession
}

/// <summary>
/// 请求人工审批（由 App 提供对话框实现）。
/// 异步：审批 UI 用 BeginInvoke + TaskCompletionSource 交给 UI 线程弹出，
/// 工具线程只 await 结果（不再同步阻塞 Dispatcher，见评估报告 §4.2）。
/// </summary>
public delegate Task<LocalApprovalDecision> LocalApprovalHandler(string command, string workingDirectory, CancellationToken ct);

/// <summary>
/// 暴露给 AI 的本机 CLI 工具：把命令送进用户可见的共享 PowerShell 终端执行。
/// 安全：硬白名单（仅白名单内的 CLI）→ 危险规则拦截 → 人工审批 → 执行并回传干净输出。
/// </summary>
public sealed class LocalTerminalTool : IAgentTool
{
    private readonly Func<string?> _sessionKey;
    private readonly Func<string, LocalTerminalSessionViewModel?> _terminalResolver;
    private readonly IAiTerminalBroker? _broker;
    private readonly LocalTerminalConfig _config;
    private readonly LocalApprovalHandler _approval;
    private readonly Action _persist;
    private readonly LocalApprovalExemptionRegistry? _exemptions;
    private readonly AITool _schema;

    public LocalTerminalTool(
        Func<string?> sessionKey,
        Func<string, LocalTerminalSessionViewModel?> terminalResolver,
        LocalTerminalConfig config,
        LocalApprovalHandler approval,
        Action persist,
        LocalApprovalExemptionRegistry? exemptions = null,
        IAiTerminalBroker? broker = null)
    {
        _sessionKey = sessionKey;
        _terminalResolver = terminalResolver;
        _broker = broker;
        _config = config;
        _approval = approval;
        _persist = persist;
        _exemptions = exemptions;
        _schema = AIFunctionFactory.Create(
            (string command, string? terminalId = null) => (command, terminalId),
            Name,
            Description);
    }

    public const string ToolName = "local_shell";

    public string Name => ToolName;

    public bool Destructive => true;

    public bool ReadOnly => false;

    public string? ToolGroup => "local";

    public AITool Tool => _schema;

    private const string Description =
        "在本机 Windows PowerShell 终端中执行一条一次性 CLI 命令（命令与输出对用户可见）。" +
        "不传 terminalId 时使用本会话共享的默认终端；传 local_shell_open 返回的 terminalId（如 term-1）时，把命令注入该终端（要求它当前空闲，长驻命令未结束时会被拒绝）。" +
        "【重要约束】同一终端同一时刻只能执行一条命令：必须等本工具返回（含输出与退出码）后再发下一条；" +
        "不要并发调用同一个终端，也不要在上一条命令尚未结束时提交新命令。" +
        "【适用】一次性、能自行结束的命令；不要用于交互式/长驻命令（如 vim、top、tail -f、watch、REPL、等待 stdin 的程序），这类会一直占住终端且会被前置拒绝。" +
        "需要长驻/后台任务（dev server、日志跟随、下载、构建等）请改用 local_shell_open，它会创建独立终端。" +
        "需要连续执行多条命令时，请**分多次调用本工具**。" +
        "每次只接受一条简单命令：禁止 `;`、`&&`、`||`、`|`、`&`、重定向、`$()`/`@()`、脚本块、括号/方括号表达式和嵌套 shell；引号内的普通文本不受影响。" +
        "仅允许白名单内的 CLI（如 git/dotnet/npm/gh/aws/az/terraform 等），且可能需用户人工审批。参数 command 为完整命令行；返回命令的 stdout/stderr 与退出码。";

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var command = arguments is not null && arguments.TryGetValue("command", out var c) ? c?.ToString()?.Trim() : null;
        var terminalId = arguments is not null && arguments.TryGetValue("terminalId", out var t)
            ? t?.ToString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(command))
            return "错误：缺少 command 参数。";

        var policy = LocalCommandPolicy.Check(command, _config.AllowedCommands);
        if (policy == LocalPolicyResult.Blocked)
        {
            await AuditAsync(command, CommandStatus.Blocked, decision: "blocked", result: "安全策略拦截");
            return "已拒绝：该命令被本机安全策略判定为危险或无效，禁止执行。";
        }
        if (policy == LocalPolicyResult.UnsafeSyntax)
        {
            await AuditAsync(command, CommandStatus.Blocked, decision: "blocked", result: "包含 shell 拼接/重定向/子表达式");
            return "已拒绝：本机命令只允许单条简单命令，禁止使用 `;`、`&&`、`||`、`|`、`&`、重定向、`$()`/`@()`、脚本块、括号/方括号表达式或嵌套 shell。请拆成多次调用。";
        }
        if (policy == LocalPolicyResult.Interactive)
        {
            await AuditAsync(command, CommandStatus.Blocked, decision: "blocked", result: "交互式/长驻命令拦截");
            return "已拒绝：检测到交互式/长驻命令（会一直占住共享终端）。请改用一次性/非交互形式，" +
                   "例如 `tail -n 50`、`ping -n 4`、`python script.py`（而不是裸 `python`）、`git --no-pager log` 等；" +
                   "需要连续执行多条命令时，请分多次调用本工具。";
        }
        if (policy == LocalPolicyResult.NotInAllowlist)
        {
            await AuditAsync(command, CommandStatus.Blocked, decision: "blocked", result: "不在本机 CLI 白名单");
            return $"已拒绝：命令 '{LocalCommandPolicy.FirstExecutable(command)}' 不在本机 CLI 白名单内。" +
                   $"允许的 CLI：{string.Join(", ", _config.AllowedCommands)}。若确需，请在「AI 助手设置 → 本机终端」中扩充白名单。";
        }

        var sessionKey = _sessionKey() ?? "default";
        LocalTerminalSessionViewModel? terminal = null;
        AiTerminalInfo? target = null;
        if (!string.IsNullOrWhiteSpace(terminalId))
        {
            if (_broker is null)
                return "错误：当前宿主不支持向指定终端注入命令。";
            target = _broker.List(sessionKey)
                .FirstOrDefault(x => string.Equals(x.Id, terminalId, StringComparison.Ordinal));
            if (target is null)
                return $"错误：未找到终端 {terminalId}。请先用 local_shell_list 查看本会话终端。";
            if (target.Running)
                return $"错误：终端 {terminalId}（{target.Name}）仍在执行上一条命令，不能注入新命令。请等它结束，或新开一个终端。";
        }
        else
        {
            terminal = _terminalResolver(sessionKey);
            if (terminal is null)
                return "错误：本机终端不可用（无法创建共享会话）。";
        }

        var approvalDirectory = terminal?.Subtitle;
        if (string.IsNullOrWhiteSpace(approvalDirectory))
        {
            approvalDirectory = string.IsNullOrWhiteSpace(LocalTerminalSettings.WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : LocalTerminalSettings.WorkingDirectory;
        }

        var autoApproved = LocalCommandPolicy.IsAutoApproved(command, _config.AutoApprovedCommands);
        if (!autoApproved && _exemptions?.IsApproved(ApprovalRoundContext.CurrentRoundId, _sessionKey()) == true)
            autoApproved = true;   // 命中弹窗勾选的「本轮次内/本次会话 允许全部命令」授权（会话级按会话键隔离）
        var manualApproval = false;
        if (!autoApproved)
        {
            var decision = await _approval(command, approvalDirectory, ct);
            switch (decision)
            {
                case LocalApprovalDecision.Deny:
                    await AuditAsync(command, CommandStatus.Rejected, decision: "manual-rejected", result: "用户拒绝");
                    return "用户拒绝执行该命令。";
                case LocalApprovalDecision.Allow:
                    manualApproval = true;
                    break;
                case LocalApprovalDecision.AlwaysAllow:
                    _config.AutoApprovedCommands = _config.AutoApprovedCommands
                        .Append(command).Distinct(StringComparer.Ordinal).ToArray();
                    _persist();
                    manualApproval = true;
                    break;
                case LocalApprovalDecision.AllowRound:
                    // 轮次信号缺失时登记失败 → 降级为仅本次放行（只会多弹窗，不会误放行）
                    _exemptions?.GrantRoundAll(ApprovalRoundContext.CurrentRoundId);
                    manualApproval = true;
                    break;
                case LocalApprovalDecision.AllowSession:
                    _exemptions?.GrantSessionAll(_sessionKey());
                    manualApproval = true;
                    break;
            }
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(_config.TimeoutSeconds, 1, 1800));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            CommandResult result;
            if (target is not null)
            {
                var run = await _broker!.RunAsync(sessionKey, target.Id, command, timeout, ct);
                if (!run.Success || run.Result is null)
                {
                    await AuditAsync(command, CommandStatus.Failed,
                        decision: manualApproval ? "manual-approved" : autoApproved ? "auto-approved" : null,
                        result: run.Error ?? "注入失败");
                    return "执行失败：" + (run.Error ?? "无法向指定终端注入命令。");
                }
                result = run.Result;
            }
            else
            {
                result = await terminal!.RunCommandAsync(command, timeout, ct);
            }
            sw.Stop();
            if (result.Busy)
                return "上一条命令仍在执行中：本机终端一次只能跑一条命令，请等它返回（输出与退出码）后再提交下一条，不要并发调用。";

            var output = result.Output ?? string.Empty;
            // 仅作本地安全硬上限（保留尾部，错误/退出信息通常在末尾）；常规截断与落盘由中央 ToolResultMaxChars/spill 处理。
            var cap = _config.MaxOutputChars;
            if (cap > 0 && output.Length > cap)
                output = "…[前部已省略]\n" + output[^cap..];

            var status = result.Killed
                ? $"超时 {timeout.TotalSeconds:0}s 且无法中断，已强制终止终端进程树（该会话终端下次将自动重开）"
                : result.TimedOut
                    ? $"超时 {timeout.TotalSeconds:0}s，已发送中断"
                    : $"退出码 {result.ExitCode}";
            var meta = $"{status} · 耗时 {sw.Elapsed.TotalSeconds:0.0}s";
            if (!string.IsNullOrWhiteSpace(result.WorkingDirectory))
                meta += $" · 工作目录 {result.WorkingDirectory}";

            // 本机终端执行入审计（评估报告待办 #8：核查确认此前零审计, 随 M1-一并补入 §10.A.1 范围）
            var auditStatus = result.ExitCode == 0 && !result.TimedOut && !result.Killed
                ? CommandStatus.Executed
                : CommandStatus.Failed;
            await AuditAsync(command, auditStatus,
                decision: manualApproval ? "manual-approved" : autoApproved ? "auto-approved" : null,
                result: status, exitCode: result.ExitCode);

            // 状态置于末尾：中央对命令输出按“保尾”截断，确保退出码/元信息可见。
            return output.Length == 0 ? $"[{meta}]（无输出）" : $"{output}\n[{meta}]";
        }
        catch (Exception ex)
        {
            await AuditAsync(command, CommandStatus.Failed,
                decision: manualApproval ? "manual-approved" : null, result: ex.Message);
            return "执行失败：" + ex.Message;
        }
    }

    /// <summary>本地终端审计（§10.A.1）：执行/拒绝均入链；失败静默, 不影响工具返回。</summary>
    private static Task AuditAsync(string command, CommandStatus status,
        string? decision = null, string? result = null, int? exitCode = null)
        => LocalAudit.LogAsync(ToolName, $"LOCAL_SHELL: {command}", status,
            result: result, decision: decision, exitCode: exitCode);
}
