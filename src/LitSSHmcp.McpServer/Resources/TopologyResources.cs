using System.ComponentModel;
using System.Text.Json;
using LitSSHmcp.Core.Services.Topology;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Resources;

/// <summary>MCP 资源：资产拓扑总览（只读），等价 <c>topology_query(action="overview")</c> 的返回。</summary>
[McpServerResourceType]
public class TopologyResources
{
    private readonly ITopologyService _topologyService;

    public TopologyResources(ITopologyService topologyService) => _topologyService = topologyService;

    [McpServerResource(UriTemplate = "litssh://topology", Name = "litssh-topology",
        Title = "资产拓扑", MimeType = "application/json")]
    [Description("资产拓扑图（服务器 / 应用 / 数据库及关系 runsOn/connectsTo/canAccess）。跨机排查的全局视图。")]
    public async Task<string> GetTopology() =>
        JsonSerializer.Serialize(await _topologyService.GetGraphAsync(), SshResources.Json);
}
