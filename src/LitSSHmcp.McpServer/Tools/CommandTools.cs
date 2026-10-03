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
    private readonly ISecurityOptionsProvider _securityOptions;

    public CommandTools(
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

    [McpServerTool(Name = "ssh_execute_command", UseStructuredContent = true, OutputSchemaType = typeof(CommandResultDto), Destructive = true, OpenWorld = true)]
    [Description("在SSH服务器执行Shell命令(查日志/进程/磁盘/网络等)。危险命令返回status=blocked, 敏感命令需人工确认(可能返回rejected/approval_timeout/approval_unavailable); SQL用mysql_*/postgres_*, Redis用redis_*; 输出最多2万字符, 超出置truncated=true")]
    public async Task<CommandResultDto> ExecuteCommand(
        [Description("服务器标识: ID/名称/主机名均可, 可用ssh_list_servers列出")] string serverId,
        [Description("要执行的Shell命令(单条), 如 'df -h'、'tail -n 100 /var/log/app.log'")] string command,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return CommandResultDto.Fail(resolveStatus!, resolveError!, "server_not_found", command);

        var filterResult = _commandFilter.CheckCommand(command);

        if (filterResult == CommandFilterResult.Blocked)
        {
            await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Command = command,
                Status = CommandStatus.Blocked
            });

            return CommandResultDto.Fail("blocked",
                "命令被安全策略禁止执行。原因: 该命令属于危险命令列表，可能对系统造成不可逆损害。",
                "blocked_command", command, server.Id, server.Name, server.Host);
        }

        if (filterResult == CommandFilterResult.Sensitive)
        {
            var outcome = await _approvalService.RequestApprovalAsync(
                ToolSupport.ServerLabel(server), command, filterResult, null, null, cancellationToken);

            if (outcome != ApprovalOutcome.Approved)
            {
                await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Command = command,
                    Status = CommandStatus.Rejected
                });

                var (status, error) = ApprovalOutcomeText.Describe(outcome, _securityOptions.Approval.TimeoutSeconds);
                return CommandResultDto.Fail(status, error, "approval", command, server.Id, server.Name, server.Host);
            }

            await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Command = command,
                Status = CommandStatus.Approved
            });
        }

        var result = await _sshService.ExecuteCommandAsync(server, command, cancellationToken);

        // 命令已在远端执行, 审计失败不能让工具报错(否则模型重试会重复执行)
        await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = command,
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

    [McpServerTool(Name = "ssh_get_command_history", UseStructuredContent = true, OutputSchemaType = typeof(CommandHistoryDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("查看SSH命令执行历史(审计), 返回的result已截断到4000字符。支持limit/offset翻页; SQL/Redis操作历史用datasource_get_sql_history")]
    public async Task<CommandHistoryDto> GetCommandHistory(
        [Description("服务器标识(可选, 留空查全部, 可用ssh_list_servers列出)")] string? serverId = null,
        [Description(ToolSupport.HistoryLimitDescription)] int limit = ToolSupport.DefaultHistoryLimit,
        [Description("跳过的条数, 与limit配合翻页(默认0)")] int offset = 0,
        [Description("会话ID(可选): 只查某个 MCP 会话产生的记录; 当前会话ID见 mcp_self_check")] string? sessionId = null,
        [Description("工具名(可选): 只查由某个 MCP 工具产生的记录, 如 ssh_execute_command / docker_logs")] string? tool = null)
    {
        var config = await _configService.LoadConfigAsync();
        string? filterId = null;
        if (!string.IsNullOrWhiteSpace(serverId))
        {
            var (server, status, error) = ToolSupport.ResolveServer(config, serverId!);
            if (server == null)
                return CommandHistoryDto.Fail(status!, error!);
            filterId = server.Id;
        }

        var effectiveLimit = ToolSupport.ClampLimit(limit);
        var records = (await _auditLogService.GetLogsAsync(
                filterId, effectiveLimit, null, false, Math.Max(0, offset), sessionId, tool))
            .ToList();

        foreach (var record in records)
        {
            if (record.Result != null && record.Result.Length > ToolSupport.MaxAuditResultChars)
                record.Result = record.Result[..ToolSupport.MaxAuditResultChars] + "...[已截断]";
        }

        return new CommandHistoryDto
        {
            Success = true,
            Count = records.Count,
            HasMore = records.Count >= effectiveLimit,
            Records = records
        };
    }
}
