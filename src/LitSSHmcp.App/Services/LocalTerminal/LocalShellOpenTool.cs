using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.Services.LocalTerminal;

public sealed class LocalShellOpenTool : IAgentTool
{
    public const string ToolName = "local_shell_open";

    private readonly Func<string?> _sessionKey;
    private readonly IAiTerminalBroker _broker;
    private readonly LocalTerminalConfig _config;
    private readonly LocalApprovalHandler _approval;
    private readonly Action _persist;
    private readonly LocalApprovalExemptionRegistry? _exemptions;
    private readonly AITool _schema;

    public LocalShellOpenTool(
        Func<string?> sessionKey,
        IAiTerminalBroker broker,
        LocalTerminalConfig config,
        LocalApprovalHandler approval,
        Action persist,
        LocalApprovalExemptionRegistry? exemptions = null)
    {
        _sessionKey = sessionKey;
        _broker = broker;
        _config = config;
        _approval = approval;
        _persist = persist;
        _exemptions = exemptions;
        _schema = AIFunctionFactory.Create(
            (string name, string command) => (name, command),
            ToolName,
            Description);
    }

    public string Name => ToolName;

    public bool Destructive => true;

    public bool ReadOnly => false;

    public string? ToolGroup => "local";

    public bool IsLocal => true;

    public AITool Tool => _schema;

    private const string Description =
        "打开一个**独立的**本机 PowerShell 会话并启动长驻命令（后台服务、dev server、日志跟随、下载、构建等），立即返回 terminalId。" +
        "与 local_shell 不同，它不会等待命令结束，也不会阻塞其他终端；一个 AI 会话可同时拥有多个终端。" +
        "【用法】启动后用 local_shell_manage(action=\"list\") 查询、local_shell_manage(action=\"read\", terminalId, cursor) 读取增量输出、local_shell_stop 停止并关闭标签。" +
        "命令结束后（终端空闲时）可用 local_shell 的 terminalId 参数向该终端注入下一条一次性命令；终端仍在跑长驻命令时会拒绝注入。" +
        "不要用它启动需要键盘交互的程序（shell、REPL、vim、docker exec -it）；这些会被拒绝。" +
        "每次只接受一条简单命令：禁止 `;`、`&&`、`||`、`|`、`&`、重定向、`$()`/`@()`、脚本块、括号/方括号表达式和嵌套 shell。" +
        "命令仍受硬白名单与危险规则约束，并可能需要人工审批。name 是便于用户识别的终端名，例如“后端”“前端”“下载”。";

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var name = arguments is not null && arguments.TryGetValue("name", out var n) ? n?.ToString()?.Trim() : null;
        var command = arguments is not null && arguments.TryGetValue("command", out var c) ? c?.ToString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(command))
            return "错误：缺少 command 参数。";
        if (string.IsNullOrWhiteSpace(name))
            name = "终端";

        var policy = LocalCommandPolicy.CheckForLongRunning(command, _config.AllowedCommands);
        if (policy == LocalPolicyResult.Blocked)
        {
            await AuditAsync(command, CommandStatus.Blocked, "blocked", "安全策略拦截（长驻）");
            return "已拒绝：该命令被本机安全策略判定为危险或无效，禁止作为长驻任务启动。";
        }
        if (policy == LocalPolicyResult.UnsafeSyntax)
        {
            await AuditAsync(command, CommandStatus.Blocked, "blocked", "包含 shell 拼接/重定向/子表达式");
            return "已拒绝：长驻命令同样只允许单条简单命令，禁止使用 `;`、`&&`、`||`、`|`、`&`、重定向、`$()`/`@()`、脚本块、括号/方括号表达式或嵌套 shell。";
        }
        if (policy == LocalPolicyResult.Interactive)
        {
            await AuditAsync(command, CommandStatus.Blocked, "blocked", "需要交互输入");
            return "已拒绝：该命令需要交互输入或全屏界面（shell、REPL、编辑器、docker exec -it 等），无法通过受控长驻会话运行。" +
                   "tail -f、dev server、构建、下载等不需要输入的命令可以使用。";
        }
        if (policy == LocalPolicyResult.NotInAllowlist)
        {
            await AuditAsync(command, CommandStatus.Blocked, "blocked", "不在本机 CLI 白名单");
            return $"已拒绝：命令 '{LocalCommandPolicy.FirstExecutable(command)}' 不在本机 CLI 白名单内。" +
                   $"允许的 CLI：{string.Join(", ", _config.AllowedCommands)}。";
        }

        var autoApproved = LocalCommandPolicy.IsAutoApproved(command, _config.AutoApprovedCommands);
        if (!autoApproved && _exemptions?.IsApproved(ApprovalRoundContext.CurrentRoundId, _sessionKey()) == true)
            autoApproved = true;

        var manualApproval = false;
        if (!autoApproved)
        {
            var workingDirectory = string.IsNullOrWhiteSpace(LocalTerminalSettings.WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : LocalTerminalSettings.WorkingDirectory;
            var decision = await _approval(command, workingDirectory, ct);
            switch (decision)
            {
                case LocalApprovalDecision.Deny:
                    await AuditAsync(command, CommandStatus.Rejected, "manual-rejected", "用户拒绝");
                    return "用户拒绝执行该命令。";
                case LocalApprovalDecision.Allow:
                    manualApproval = true;
                    break;
                case LocalApprovalDecision.AlwaysAllow:
                    _config.AutoApprovedCommands = _config.AutoApprovedCommands
                        .Append(AutoApprovedCommandRule.Create(command)).Distinct(StringComparer.Ordinal).ToArray();
                    _persist();
                    manualApproval = true;
                    break;
                case LocalApprovalDecision.AllowRound:
                    _exemptions?.GrantRoundAll(ApprovalRoundContext.CurrentRoundId);
                    manualApproval = true;
                    break;
                case LocalApprovalDecision.AllowSession:
                    _exemptions?.GrantSessionAll(_sessionKey());
                    manualApproval = true;
                    break;
            }
        }

        try
        {
            var key = _sessionKey() ?? "default";
            var result = await _broker.OpenAsync(key, name, command, TimeSpan.FromHours(8), _config.MaxSessions, ct);
            if (!result.Success || result.Info is null)
            {
                await AuditAsync(command, CommandStatus.Failed, manualApproval ? "manual-approved" : "auto-approved",
                    result.Error ?? "启动失败");
                return "启动失败：" + (result.Error ?? "未知错误");
            }

            var info = result.Info;
            await AuditAsync(command, CommandStatus.Executed,
                manualApproval ? "manual-approved" : "auto-approved", "长驻终端已启动");
            return $"已打开终端 {info.Id}（{info.Name}），pid {info.ProcessId}，状态 {info.Status}。" +
                   $"之后用 local_shell_manage(action=\"read\", terminalId=\"{info.Id}\", cursor=0) 读取输出，用 local_shell_stop(terminalId=\"{info.Id}\") 停止。";
        }
        catch (Exception ex)
        {
            await AuditAsync(command, CommandStatus.Failed,
                manualApproval ? "manual-approved" : "auto-approved", ex.Message);
            return "启动失败：" + ex.Message;
        }
    }

    private static Task AuditAsync(string command, CommandStatus status,
        string? decision = null, string? result = null, int? exitCode = null)
        => LocalAudit.LogAsync(ToolName, $"LOCAL_SHELL_OPEN: {command}", status,
            result: result, decision: decision, exitCode: exitCode);
}
