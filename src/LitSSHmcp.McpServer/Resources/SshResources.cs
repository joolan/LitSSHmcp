using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Resources;

/// <summary>
/// MCP 资源：已配置的 SSH 服务器清单（只读，供客户端“挂载/浏览”为上下文）。
/// **不含任何密码/密钥信息**；禁用服务器已排除。
/// </summary>
[McpServerResourceType]
public class SshResources
{
    private readonly IConfigService _configService;

    public SshResources(IConfigService configService) => _configService = configService;

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [McpServerResource(UriTemplate = "litssh://servers", Name = "litssh-servers",
        Title = "SSH 服务器清单", MimeType = "application/json")]
    [Description("已配置的 SSH 服务器清单（Id/名称/主机/端口/用户名/标签/分组）；不含密码，禁用项已排除。需要 serverId 时可直接引用。")]
    public async Task<string> GetServers()
    {
        var config = await _configService.LoadConfigAsync();
        var servers = config.Servers
            .Where(s => !s.Disabled)
            .Select(s => new
            {
                id = s.Id,
                name = s.Name,
                host = s.Host,
                port = s.Port,
                username = s.Username,
                group = string.IsNullOrWhiteSpace(s.Group) ? null : s.Group,
                tags = s.Tags,
                description = s.Description
            })
            .ToArray();

        return JsonSerializer.Serialize(new { count = servers.Length, servers }, Json);
    }
}
