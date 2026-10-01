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
    [Description("列出已配置的SSH服务器。数据源列表用datasource_list")]
    public async Task<SshServerListDto> ListServers()
    {
        var config = await _configService.LoadConfigAsync();
        var servers = config.Servers.Select(s => new SshServerSummaryDto
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

        return new SshServerListDto { Success = true, Count = servers.Count, Servers = servers };
    }

    [McpServerTool(Name = "ssh_get_server_status", UseStructuredContent = true, OutputSchemaType = typeof(ServerStatusDto), ReadOnly = true, OpenWorld = true)]
    [Description("获取SSH服务器连接状态(是否在线)")]
    public async Task<ServerStatusDto> GetServerStatus(
        [Description("服务器ID, 可用ssh_list_servers列出")] string serverId)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return ServerStatusDto.Fail($"Server not found: {serverId}");

        var isConnected = await _sshService.TestConnectionAsync(server);
        return new ServerStatusDto
        {
            Success = true,
            Id = server.Id,
            Name = server.Name,
            Host = server.Host,
            Status = isConnected ? "Connected" : "Disconnected"
        };
    }

    [McpServerTool(Name = "ssh_test_connection", UseStructuredContent = true, OutputSchemaType = typeof(SshTestConnectionDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("测试SSH服务器连通性。数据源连通性用datasource_test_connection")]
    public async Task<SshTestConnectionDto> TestConnection(
        [Description("服务器ID, 可用ssh_list_servers列出")] string serverId)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return SshTestConnectionDto.Fail($"Server not found: {serverId}");

        var success = await _sshService.TestConnectionAsync(server);
        return new SshTestConnectionDto { Success = success, ServerId = serverId, Name = server.Name };
    }
}
