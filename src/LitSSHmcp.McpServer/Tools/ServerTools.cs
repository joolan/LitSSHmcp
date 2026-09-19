using System.ComponentModel;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
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

    [McpServerTool]
    [Description("List all configured SSH servers")]
    public async Task<string> ListServers()
    {
        var config = await _configService.LoadConfigAsync();
        var servers = config.Servers.Select(s => new
        {
            s.Id,
            s.Name,
            s.Host,
            s.Port,
            s.Username,
            s.AuthType,
            s.Description,
            s.Tags,
            s.LastConnectedAt
        });
        return JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool]
    [Description("Get connection status of an SSH server")]
    public async Task<string> GetServerStatus(
        [Description("Server ID")] string serverId)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return JsonSerializer.Serialize(new { error = $"Server not found: {serverId}" });

        var isConnected = await _sshService.TestConnectionAsync(server);
        return JsonSerializer.Serialize(new
        {
            server.Id,
            server.Name,
            server.Host,
            Status = isConnected ? "Connected" : "Disconnected"
        });
    }

    [McpServerTool]
    [Description("Test SSH connection to a server")]
    public async Task<string> TestConnection(
        [Description("Server ID")] string serverId)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return JsonSerializer.Serialize(new { success = false, error = $"Server not found: {serverId}" });

        var success = await _sshService.TestConnectionAsync(server);
        return JsonSerializer.Serialize(new { success, serverId, server.Name });
    }
}