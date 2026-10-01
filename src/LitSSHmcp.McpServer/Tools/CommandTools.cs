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

    [McpServerTool(Name = "ssh_execute_command", UseStructuredContent = true, OutputSchemaType = typeof(CommandResultDto), Destructive = true, OpenWorld = true)]
    [Description("在SSH服务器执行Shell命令(查日志/进程/磁盘/网络等)。危险命令拒绝, 敏感命令需桌面确认; SQL用mysql_*, Redis用redis_*")]
    public async Task<CommandResultDto> ExecuteCommand(
        [Description("服务器ID, 可用ssh_list_servers列出")] string serverId,
        [Description("要执行的Shell命令(单条), 如 'df -h'、'tail -n 100 /var/log/app.log'")] string command)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return CommandResultDto.Fail("server_not_found", $"服务器未找到: {serverId}");

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

            return CommandResultDto.Fail("blocked",
                "命令被安全策略禁止执行。原因: 该命令属于危险命令列表，可能对系统造成不可逆损害。",
                "blocked_command", command);
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

                return CommandResultDto.Fail("rejected",
                    "敏感命令被用户拒绝执行。该命令需要用户手动确认后才能执行。",
                    "user_rejected", command);
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

    [McpServerTool(Name = "ssh_get_command_history", UseStructuredContent = true, OutputSchemaType = typeof(CommandHistoryDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("查看SSH命令执行历史(审计)。SQL/Redis操作历史用datasource_get_sql_history")]
    public async Task<CommandHistoryDto> GetCommandHistory(
        [Description("服务器ID(可选, 留空查全部, 可用ssh_list_servers列出)")] string? serverId = null,
        [Description("返回条数(默认50)")] int limit = 50)
    {
        var records = (await _auditLogService.GetLogsAsync(serverId, limit)).ToList();
        return new CommandHistoryDto { Success = true, Count = records.Count, Records = records };
    }
}
