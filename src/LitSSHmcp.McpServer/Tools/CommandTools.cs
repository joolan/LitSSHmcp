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
public class CommandTools
{
    private readonly IConfigService _configService;
    private readonly ISshService _sshService;
    private readonly ICommandFilterService _commandFilter;
    private readonly IApprovalService _approvalService;
    private readonly IAuditLogService _auditLogService;

    public CommandTools(
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
    [Description("Execute a shell command on an SSH server")]
    public async Task<string> ExecuteCommand(
        [Description("Server ID")] string serverId,
        [Description("Command to execute")] string command)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return JsonSerializer.Serialize(new { success = false, error = $"服务器未找到: {serverId}", status = "server_not_found" });

        var filterResult = _commandFilter.CheckCommand(command);

        if (filterResult == CommandFilterResult.Blocked)
        {
            await _auditLogService.LogCommandAsync(new CommandAuditLog
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Command = command,
                Status = CommandStatus.Blocked
            });

            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"命令被安全策略禁止执行。原因: 该命令属于危险命令列表，可能对系统造成不可逆损害。",
                status = "blocked",
                reason = "blocked_command",
                command = command
            });
        }

        if (filterResult == CommandFilterResult.Sensitive)
        {
            var approved = await _approvalService.RequestApprovalAsync(server.Name, command, filterResult);
            if (!approved)
            {
                await _auditLogService.LogCommandAsync(new CommandAuditLog
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Command = command,
                    Status = CommandStatus.Rejected
                });

                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"敏感命令被用户拒绝执行。该命令需要用户手动确认后才能执行。",
                    status = "rejected",
                    reason = "user_rejected",
                    command = command
                });
            }

            await _auditLogService.LogCommandAsync(new CommandAuditLog
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Command = command,
                Status = CommandStatus.Approved
            });
        }

        var result = await _sshService.ExecuteCommandAsync(server, command);

        await _auditLogService.LogCommandAsync(new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = command,
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
    [Description("Get command execution history")]
    public async Task<string> GetCommandHistory(
        [Description("Server ID (optional)")] string? serverId = null,
        [Description("Number of records to return")] int limit = 50)
    {
        var logs = await _auditLogService.GetLogsAsync(serverId, limit);
        return JsonSerializer.Serialize(logs, new JsonSerializerOptions { WriteIndented = true });
    }
}