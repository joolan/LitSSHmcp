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

    public SudoTools(
        IConfigService configService,
        ISshService sshService,
        ICommandFilterService commandFilter,
        IApprovalService approvalService,
        IAuditLogService auditLogService)
    {
        _configService = configService;
        _sshService = sshService;
        _commandFilter = commandFilter;
        _approvalService = approvalService;
        _auditLogService = auditLogService;
    }

    [McpServerTool(Name = "ssh_execute_sudo", UseStructuredContent = true, OutputSchemaType = typeof(CommandResultDto), Destructive = true, OpenWorld = true)]
    [Description("以sudo提权执行命令(启停服务/装软件/改系统配置或权限等)。需已配置提权+桌面确认; 需提权时直接用它, 别先试ssh_execute_command")]
    public async Task<CommandResultDto> ExecuteWithSudo(
        [Description("服务器ID, 可用ssh_list_servers列出")] string serverId,
        [Description("要以sudo执行的命令")] string command)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return CommandResultDto.Fail("server_not_found", $"服务器未找到: {serverId}");

        if (server.SudoType == SudoType.None)
            return CommandResultDto.Fail("sudo_not_configured",
                "该服务器未配置提权功能。请在服务器配置中设置SudoType和相关密码。");

        var filterResult = _commandFilter.CheckCommand(command);

        if (filterResult == CommandFilterResult.Blocked)
        {
            await _auditLogService.LogCommandAsync(new CommandAuditLog
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Command = $"SUDO: {command}",
                Status = CommandStatus.Blocked
            });

            return CommandResultDto.Fail("blocked",
                "提权命令被安全策略禁止。原因: 该命令属于危险命令列表，可能对系统造成不可逆损害。",
                "blocked_command", command);
        }

        var approved = await _approvalService.RequestApprovalAsync(
            server.Name,
            $"提权执行: {command}",
            CommandFilterResult.Sensitive);

        if (!approved)
        {
            await _auditLogService.LogCommandAsync(new CommandAuditLog
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Command = $"SUDO: {command}",
                Status = CommandStatus.Rejected
            });

            return CommandResultDto.Fail("rejected",
                "提权操作被用户拒绝。需要用户手动确认后才能执行提权命令。",
                "user_rejected", command);
        }

        await _auditLogService.LogCommandAsync(new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = $"SUDO: {command}",
            Status = CommandStatus.Approved
        });

        var result = await _sshService.ExecuteWithSudoAsync(server, command);

        await _auditLogService.LogCommandAsync(new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = $"SUDO: {command}",
            Result = result.Output,
            Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            ExitCode = result.ExitCode
        });

        return new CommandResultDto
        {
            Success = result.Success,
            Status = result.Success ? null : "failed",
            Error = result.Error,
            Output = result.Output,
            ExitCode = result.ExitCode,
            DurationMs = result.Duration.TotalMilliseconds
        };
    }

    [McpServerTool(Name = "ssh_get_sudo_status", UseStructuredContent = true, OutputSchemaType = typeof(SudoStatusDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("查看SSH服务器的提权(sudo)配置")]
    public async Task<SudoStatusDto> GetSudoStatus(
        [Description("服务器ID, 可用ssh_list_servers列出")] string serverId)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return SudoStatusDto.Fail($"服务器未找到: {serverId}");

        return new SudoStatusDto
        {
            Success = true,
            ServerId = server.Id,
            ServerName = server.Name,
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
            _ => "未知"
        };
    }
}
