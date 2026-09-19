using System.ComponentModel;
using System.Text.Json;
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

    [McpServerTool]
    [Description("Execute a command with sudo privileges (for permission denied errors)")]
    public async Task<string> ExecuteWithSudo(
        [Description("Server ID")] string serverId,
        [Description("Command to execute with sudo")] string command)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return JsonSerializer.Serialize(new { success = false, error = $"服务器未找到: {serverId}", status = "server_not_found" });

        if (server.SudoType == SudoType.None)
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "该服务器未配置提权功能。请在服务器配置中设置SudoType和相关密码。",
                status = "sudo_not_configured"
            });

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

            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"提权命令被安全策略禁止。原因: 该命令属于危险命令列表，可能对系统造成不可逆损害。",
                status = "blocked",
                reason = "blocked_command",
                command = command
            });
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

            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "提权操作被用户拒绝。需要用户手动确认后才能执行提权命令。",
                status = "rejected",
                reason = "user_rejected",
                command = command
            });
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

        return JsonSerializer.Serialize(new
        {
            success = result.Success,
            output = result.Output,
            error = result.Error,
            exitCode = result.ExitCode,
            durationMs = result.Duration.TotalMilliseconds
        });
    }

    [McpServerTool]
    [Description("Get sudo configuration status for a server")]
    public async Task<string> GetSudoStatus(
        [Description("Server ID")] string serverId)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return JsonSerializer.Serialize(new { success = false, error = $"服务器未找到: {serverId}" });

        return JsonSerializer.Serialize(new
        {
            success = true,
            serverId = server.Id,
            serverName = server.Name,
            sudoType = server.SudoType.ToString(),
            sudoUsername = server.SudoUsername,
            isConfigured = server.SudoType != SudoType.None,
            description = GetSudoDescription(server.SudoType)
        }, new JsonSerializerOptions { WriteIndented = true });
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