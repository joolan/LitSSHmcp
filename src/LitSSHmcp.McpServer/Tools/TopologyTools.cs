// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class TopologyTools
{
    private readonly ITopologyService _topologyService;
    private readonly IConfigService _configService;

    public TopologyTools(ITopologyService topologyService, IConfigService configService)
    {
        _topologyService = topologyService;
        _configService = configService;
    }

    [McpServerTool(Name = "topology_get_overview", UseStructuredContent = true, OutputSchemaType = typeof(TopologyGraph), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("获取资产拓扑图(服务器/应用/数据库及关系)。跨机排查前先看它")]
    public async Task<TopologyGraph> GetTopology() => await _topologyService.GetGraphAsync();

    [McpServerTool(Name = "topology_get_dependencies", UseStructuredContent = true, OutputSchemaType = typeof(DependencyGraph), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("查询某资产的上下游依赖(跑在哪/连了谁)。assetId支持 ssh:xx/ds:xx/app:xx 或纯ID/名称")]
    public async Task<DependencyGraph> GetAssetDependencies(
        [Description("资产ID, 如 ds:mysql-order-01 / ssh:web-server-01 / app:order-service 或纯ID/名称")] string assetId)
        => await _topologyService.GetDependenciesAsync(assetId);

    [McpServerTool(Name = "topology_discover", UseStructuredContent = true, OutputSchemaType = typeof(DiscoverResultDto), OpenWorld = true)]
    [Description("自动发现并补全拓扑(java进程/网络连接/JDBC配置/processlist)。拓扑缺失或过期时用")]
    public async Task<DiscoverResultDto> DiscoverTopology(
        [Description("限定扫描的服务器ID或名称, 逗号分隔; 留空扫描全部")] string? serverIds = null,
        [Description("配置文件搜索路径, 空格分隔; 留空使用配置中允许的路径")] string? searchPaths = null)
    {
        var config = await _configService.LoadConfigAsync();
        var allowedPaths = config.Security.Discovery.AllowedSearchPaths ?? Array.Empty<string>();

        var servers = Split(serverIds);
        var requestedPaths = Split(searchPaths, separator: ' ');

        string[]? paths;
        if (requestedPaths == null || requestedPaths.Length == 0)
        {
            paths = allowedPaths;
        }
        else
        {
            var invalid = requestedPaths.Where(p => !PathPolicy.IsRemotePathAllowed(p, allowedPaths)).ToArray();
            if (invalid.Length > 0)
            {
                return DiscoverResultDto.Fail("path_not_allowed",
                    $"以下扫描路径不在允许范围: {string.Join(", ", invalid)}。允许路径: {string.Join(", ", allowedPaths)}");
            }

            paths = requestedPaths;
        }

        var result = await _topologyService.DiscoverAsync(servers, paths);
        return new DiscoverResultDto { Success = true, Result = result };
    }

    private static string[]? Split(string? value, char separator = ',')
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }
}
