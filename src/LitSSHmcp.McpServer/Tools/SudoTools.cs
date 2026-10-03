// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class SudoTools
{
    private readonly IConfigService _configService;
    private readonly ISshService _sshService;
    private readonly ICommandFilterService _commandFilter;
    private readonly IApprovalService _approvalService;
    private readonly IAuditLogService _auditLogService;
    private readonly ISecurityOptionsProvider _securityOptions;

    public SudoTools(
        IConfigService configService,
        ISshService sshService,
        ICommandFilterService commandFilter,
        IApprovalService approvalService,
        IAuditLogService auditLogService,
        ISecurityOptionsProvider securityOptions)
    {
        _configService = configService;
        _sshService = sshService;
        _commandFilter = commandFilter;
        _approvalService = approvalService;
        _auditLogService = auditLogService;
        _securityOptions = securityOptions;
    }

    [McpServerTool(Name = "ssh_execute_sudo", UseStructuredContent = true, OutputSchemaType = typeof(CommandResultDto), Destructive = true, OpenWorld = true)]
    [Description("以sudo提权执行命令(启停服务/装软件/改系统配置或权限等)。总是需要人工确认, 可能返回status=rejected/approval_timeout/approval_unavailable; 未配置提权返回sudo_not_configured。判断是否需要提权: 无需预检, 直接用它, 失败会明确告诉你原因")]
    public async Task<CommandResultDto> ExecuteWithSudo(
        [Description("服务器标识: ID/名称/主机名均可, 可用ssh_list_servers列出")] string serverId,
        [Description("要以sudo执行的命令(单条)")] string command,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return CommandResultDto.Fail(resolveStatus!, resolveError!, "server_not_found", command);

        if (server.SudoType == SudoType.None)
            return CommandResultDto.Fail("sudo_not_configured",
                $"服务器 {server.Name} 未配置提权(SudoType=None)。请在桌面 App 的服务器配置中设置 SudoType 与密码, " +
                "或改用 ssh_execute_command(仅普通权限)。", "not_configured", command, server.Id, server.Name, server.Host);

        var filterResult = _commandFilter.CheckCommand(command);

        if (filterResult == CommandFilterResult.Blocked)
        {
            await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Command = $"SUDO: {command}",
                Status = CommandStatus.Blocked
            });

            return CommandResultDto.Fail("blocked",
                "提权命令被安全策略禁止。原因: 该命令属于危险命令列表，可能对系统造成不可逆损害。",
                "blocked_command", command, server.Id, server.Name, server.Host);
        }

        var outcome = await _approvalService.RequestApprovalAsync(
            ToolSupport.ServerLabel(server),
            $"提权执行: {command}",
            CommandFilterResult.Sensitive,
            null,
            "sudo 提权",
            cancellationToken);

        if (outcome != ApprovalOutcome.Approved)
        {
            await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Command = $"SUDO: {command}",
                Status = CommandStatus.Rejected
            });

            var (status, error) = ApprovalOutcomeText.Describe(outcome, _securityOptions.Approval.TimeoutSeconds);
            return CommandResultDto.Fail(status, error, "approval", command, server.Id, server.Name, server.Host);
        }

        await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = $"SUDO: {command}",
            Status = CommandStatus.Approved
        });

        var result = await _sshService.ExecuteWithSudoAsync(server, command, cancellationToken);

        await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = $"SUDO: {command}",
            Result = result.Output.Length > 0
                ? (result.Output.Length > ToolSupport.MaxAuditResultChars
                    ? result.Output[..ToolSupport.MaxAuditResultChars]
                    : result.Output)
                : result.Error,
            Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            ExitCode = result.ExitCode
        });

        var (output, truncated, originalLength) = ToolSupport.Truncate(result.Output, ToolSupport.MaxOutputChars);
        return new CommandResultDto
        {
            Success = result.Success,
            Status = result.Success ? null : ToolSupport.CommandFailureStatus(result),
            Error = result.Error,
            ServerId = server.Id,
            ServerName = server.Name,
            Host = server.Host,
            Output = output,
            Truncated = truncated,
            OutputChars = originalLength,
            ExitCode = result.ExitCode,
            DurationMs = result.Duration.TotalMilliseconds
        };
    }

    [McpServerTool(Name = "ssh_get_sudo_status", UseStructuredContent = true, OutputSchemaType = typeof(SudoStatusDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("查看某服务器是否已配置sudo提权及提权方式。仅在需要向用户解释'为什么不能提权'时用; 执行提权直接用ssh_execute_sudo")]
    public async Task<SudoStatusDto> GetSudoStatus(
        [Description("服务器标识: ID/名称/主机名均可, 可用ssh_list_servers列出")] string serverId)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, status, error) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return SudoStatusDto.Fail(error!);

        return new SudoStatusDto
        {
            Success = true,
            ServerId = server.Id,
            ServerName = server.Name,
            Host = server.Host,
            SudoType = server.SudoType.ToString(),
            SudoUsername = server.SudoUsername,
            IsConfigured = server.SudoType != SudoType.None,
            Description = GetSudoDescription(server.SudoType)
        };
    }

    private static string GetSudoDescription(SudoType type)
    {
        return type switch
        {
            SudoType.None => "未配置提权",
            SudoType.CurrentUser => "使用当前SSH用户密码进行sudo",
            SudoType.RootUser => "切换到root用户（需要root密码）",
            SudoType.CustomUser => "切换到指定用户（需要该用户密码）",
            SudoType.Auto => "自动: 先 sudo(当前用户密码), 失败再 su - root(同一密码)",
            _ => "未知"
        };
    }
}
