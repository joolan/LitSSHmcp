using System.ComponentModel;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Resources;

/// <summary>
/// MCP 资源：已配置的数据源清单（只读）。**不含密码**；可据此拿 datasourceId。
/// </summary>
[McpServerResourceType]
public class DatasourceResources
{
    private readonly IConfigService _configService;

    public DatasourceResources(IConfigService configService) => _configService = configService;

    [McpServerResource(UriTemplate = "litssh://datasources", Name = "litssh-datasources",
        Title = "数据源清单", MimeType = "application/json")]
    [Description("已配置的 MySQL / PostgreSQL / Redis 数据源清单（Id/名称/类型/主机/端口/用户名/默认库/访问方式）；不含密码。")]
    public async Task<string> GetDatasources()
    {
        var config = await _configService.LoadConfigAsync();
        var datasources = config.DataSources
            .Select(d => new
            {
                id = d.Id,
                name = d.Name,
                type = d.Type,
                host = d.Host,
                port = d.Port,
                username = d.Username,
                defaultDatabase = d.DefaultDatabase,
                accessMode = d.AccessMode == AccessMode.SshTunnel ? "sshTunnel" : "direct",
                tunnelServerId = d.TunnelServerId,
                readOnly = d.ReadOnly,
                tags = d.Tags,
                description = d.Description
            })
            .ToArray();

        return JsonSerializer.Serialize(new { count = datasources.Length, datasources }, SshResources.Json);
    }
}
