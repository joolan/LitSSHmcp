// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class ServiceTools
{
    private static readonly Regex ServiceName = new("^[A-Za-z0-9_.@:-]+$", RegexOptions.Compiled);

    private readonly IGuardedCommandService _runner;

    public ServiceTools(IGuardedCommandService runner)
    {
        _runner = runner;
    }

    [McpServerTool(Name = "service_status", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), ReadOnly = true, OpenWorld = true)]
    [Description("查看systemd服务状态(运行/退出码/最近日志)。服务异常先看它; 服务日志用service_logs, 重启用service_restart")]
    public async Task<RemoteCommandResultDto> ServiceStatus(
        [Description("服务器标识: ID/名称/主机名, 可用ssh_list_servers列出")] string serverId,
        [Description("服务名(不带.service) 或 unit, 如 nginx / docker / myapp.service")] string service,
        CancellationToken cancellationToken = default)
    {
        if (!ServiceName.IsMatch(service))
            return new RemoteCommandResultDto { Success = false, Status = "invalid_argument", Error = $"非法的服务名: {service}" };

        var command = $"systemctl status --no-pager -- {ShellQuote.Single(service)}";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }

    [McpServerTool(Name = "service_list", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("列出systemd服务单元(可按关键字过滤, 返回名称/状态/描述)。不确定服务名时先列出来")]
    public async Task<RemoteCommandResultDto> ServiceList(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("可选关键字过滤(对 unit 名做 grep -i), 如 nginx / mysvc")] string? pattern = null,
        CancellationToken cancellationToken = default)
    {
        var baseCmd = "systemctl list-units --type=service --all --no-pager --no-legend";
        var command = string.IsNullOrWhiteSpace(pattern)
            ? baseCmd
            : $"{baseCmd} | grep -i -- {ShellQuote.Single(pattern)}";

        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }

    [McpServerTool(Name = "service_restart", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), Destructive = true, OpenWorld = true)]
    [Description("重启systemd服务(会短暂中断服务, 需人工确认)。确认服务名用service_list, 重启后看service_status")]
    public async Task<RemoteCommandResultDto> ServiceRestart(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("服务名(不带.service) 或 unit")] string service,
        CancellationToken cancellationToken = default)
    {
        if (!ServiceName.IsMatch(service))
            return new RemoteCommandResultDto { Success = false, Status = "invalid_argument", Error = $"非法的服务名: {service}" };

        var command = $"systemctl restart -- {ShellQuote.Single(service)}";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }

    [McpServerTool(Name = "service_logs", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), ReadOnly = true, OpenWorld = true)]
    [Description("查看systemd服务日志(journalctl -u)。用lines限制行数, since按时间过滤(如 '1h'/'2024-01-01 10:00:00')")]
    public async Task<RemoteCommandResultDto> ServiceLogs(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("服务名(不带.service) 或 unit")] string service,
        [Description("返回最近的行数(默认200, 上限5000)")] int lines = 200,
        [Description("起始时间(可选), 如 1h / yesterday / '2024-01-01 10:00:00'")] string? since = null,
        CancellationToken cancellationToken = default)
    {
        if (!ServiceName.IsMatch(service))
            return new RemoteCommandResultDto { Success = false, Status = "invalid_argument", Error = $"非法的服务名: {service}" };

        lines = Math.Clamp(lines, 1, 5000);
        var sinceArg = string.IsNullOrWhiteSpace(since)
            ? string.Empty
            : $" --since {ShellQuote.Single(since)}";

        var command = $"journalctl -u {ShellQuote.Single(service)} -n {lines} --no-pager{sinceArg}";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }
}
