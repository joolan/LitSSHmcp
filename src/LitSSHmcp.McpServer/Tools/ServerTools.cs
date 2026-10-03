// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class ServerTools
{
    private readonly IConfigService _configService;
    private readonly ISshService _sshService;
    private readonly IAuditLogService _auditLogService;

    public ServerTools(IConfigService configService, ISshService sshService, IAuditLogService auditLogService)
    {
        _configService = configService;
        _sshService = sshService;
        _auditLogService = auditLogService;
    }

    [McpServerTool(Name = "ssh_list_servers", UseStructuredContent = true, OutputSchemaType = typeof(SshServerListDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("列出已配置的SSH服务器(返回id/name/host, 后续工具传这些值即可)。数据源列表用datasource_list")]
    public async Task<SshServerListDto> ListServers()
    {
        var config = await _configService.LoadConfigAsync();
        var servers = config.Servers.Where(s => !s.Disabled).Select(s => new SshServerSummaryDto
        {
            Id = s.Id,
            Name = s.Name,
            Host = s.Host,
            Port = s.Port,
            Username = s.Username,
            AuthType = s.AuthType.ToString(),
            Description = s.Description,
            Tags = s.Tags,
            LastConnectedAt = s.LastConnectedAt
        }).ToList();

        await LogAuditAsync(string.Empty, string.Empty, "LIST_SERVERS", $"{servers.Count} servers", CommandStatus.Executed, AuditCategory.Meta);
        return new SshServerListDto { Success = true, Count = servers.Count, Servers = servers };
    }

    [McpServerTool(Name = "ssh_get_server_status", UseStructuredContent = true, OutputSchemaType = typeof(ServerStatusDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("探测SSH服务器是否在线, 失败时给出固定分类(auth_failed/host_key_mismatch/timeout/connection_error)。全面连通性诊断用ssh_test_connection")]
    public async Task<ServerStatusDto> GetServerStatus(
        [Description("服务器标识: ID/名称/主机名均可, 可用ssh_list_servers列出")] string serverId,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return ServerStatusDto.Fail(resolveStatus ?? "server_not_found", resolveError!);

        var probe = await _sshService.ProbeConnectionAsync(server, cancellationToken);
        await LogAuditAsync(server.Id, server.Name, "GET_STATUS",
            probe.Success ? "connected" : probe.ErrorKind, probe.Success ? CommandStatus.Executed : CommandStatus.Failed, AuditCategory.Probe);
        return new ServerStatusDto
        {
            Success = probe.Success,
            Status = probe.Success ? "connected" : ToolSupport.ConnectionFailureStatus(probe.ErrorKind),
            Error = probe.Success ? null : probe.Error,
            ErrorKind = probe.Success ? null : probe.ErrorKind,
            Id = server.Id,
            Name = server.Name,
            Host = server.Host,
            DurationMs = probe.Duration.TotalMilliseconds
        };
    }

    [McpServerTool(Name = "ssh_test_connection", UseStructuredContent = true, OutputSchemaType = typeof(SshTestConnectionDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("测试SSH连通性并区分失败原因: auth_failed(账号密码/密钥错)/host_key_mismatch(主机密钥变化, 可能是中间人, 需先核对指纹)/timeout/connection_error。连不上服务器时先用它")]
    public async Task<SshTestConnectionDto> TestConnection(
        [Description("服务器标识: ID/名称/主机名均可, 可用ssh_list_servers列出")] string serverId,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return SshTestConnectionDto.Fail(resolveStatus!, resolveError!);

        var probe = await _sshService.ProbeConnectionAsync(server, cancellationToken);
        await LogAuditAsync(server.Id, server.Name, "TEST_CONNECTION",
            probe.Success ? "connected" : probe.ErrorKind, probe.Success ? CommandStatus.Executed : CommandStatus.Failed, AuditCategory.Probe);
        if (probe.Success)
        {
            return new SshTestConnectionDto
            {
                Success = true,
                Status = "connected",
                ServerId = server.Id,
                Name = server.Name,
                Host = server.Host,
                DurationMs = probe.Duration.TotalMilliseconds
            };
        }

        return new SshTestConnectionDto
        {
            Success = false,
            Status = ToolSupport.ConnectionFailureStatus(probe.ErrorKind),
            ErrorKind = probe.ErrorKind,
            Error = $"{probe.Error}（{ExplainFailure(probe.ErrorKind)}）",
            ServerId = server.Id,
            Name = server.Name,
            Host = server.Host,
            DurationMs = probe.Duration.TotalMilliseconds
        };
    }

    private static string ExplainFailure(string errorKind) => errorKind switch
    {
        "auth" => "认证失败: 检查用户名/密码/私钥口令",
        "host_key" => "主机密钥不匹配: 可能是服务器重装或中间人攻击, 请先人工核对 SSH 指纹再更新 known_hosts",
        "timeout" => "连接超时: 检查网络、防火墙或服务器负载",
        "network" => "网络不可达: 检查主机地址、端口与网络连通性",
        _ => "未知错误"
    };

    private Task LogAuditAsync(string serverId, string serverName, string command, string? result, CommandStatus status, AuditCategory category) =>
        ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = serverId,
            ServerName = serverName,
            Command = command,
            Result = result,
            Status = status,
            Category = category
        });
}
