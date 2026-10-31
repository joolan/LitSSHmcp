// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + mcp_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

/// <summary>
/// 资产图查询工具（P0）：在现有拓扑图上做搜索/邻域/路径/影响面/证据解释。
/// 全部只读，不改数据库、不执行远端命令。
/// </summary>
[McpServerToolType]
public class GraphTools
{
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    private readonly IAssetGraphQueryService _query;
    private readonly ITopologyService _topologyService;
    private readonly IUrlResolutionService _urlResolutionService;
    private readonly IAuditLogService _auditLogService;

    public GraphTools(
        IAssetGraphQueryService query,
        ITopologyService topologyService,
        IUrlResolutionService urlResolutionService,
        IAuditLogService auditLogService)
    {
        _query = query;
        _topologyService = topologyService;
        _urlResolutionService = urlResolutionService;
        _auditLogService = auditLogService;
    }

    [McpServerTool(Name = "topology_query", UseStructuredContent = true, OutputSchemaType = typeof(TopologyQueryResultDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("人工拓扑统一查询：action=overview/search/neighbors/path/impact/explain/dependencies/resolve_url；result 为对应动作结果")]
    public async Task<TopologyQueryResultDto> TopologyQuery(
        [Description("查询类型：overview / search / neighbors / path / impact / explain / dependencies / resolve_url")] string action,
        [Description("search 的搜索词：名称/ID/主机/端口/标签片段")] string? query = null,
        [Description("neighbors/impact/explain/dependencies 的资产 ID/名称")] string? assetId = null,
        [Description("path 的起点资产 ID/名称")] string? from = null,
        [Description("path 的终点资产 ID/名称")] string? to = null,
        [Description("resolve_url 的访问 URL")] string? url = null,
        [Description("search 的类型过滤，如 ssh,datasource,application,domain；留空=全部")] string? kinds = null,
        [Description("neighbors 的关系类型过滤，如 runsOn,runsIn,connectsTo,canAccess,upstreamOf,accessedVia,relatedTo；留空=全部")] string? relationTypes = null,
        [Description("explain 的关系类型过滤，如 runsOn")] string? relationType = null,
        [Description("explain 可选：只看与另一资产之间的边")] string? targetAssetId = null,
        [Description("search 返回上限，默认20，最大50")] int limit = 20,
        [Description("neighbors 跳数，默认1，最大6")] int depth = 1,
        [Description("path 最大跳数，默认6，最大6")] int maxHops = 6,
        [Description("impact 最大反向跳数，默认6，最大6")] int maxDepth = 6,
        CancellationToken cancellationToken = default)
    {
        var kind = action.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "overview":
                var overview = await GetOverview();
                return Wrap(kind, $"{overview.Nodes.Length} 个节点 / {overview.Edges.Length} 条关系", null, overview);
            case "search":
                if (string.IsNullOrWhiteSpace(query))
                    return Fail(kind, "invalid_argument", "action=search 时必须提供 query。");
                var search = await Search(query, kinds, limit, cancellationToken);
                return Wrap(kind, search.Summary, search.Error, search);
            case "neighbors":
                if (string.IsNullOrWhiteSpace(assetId))
                    return Fail(kind, "invalid_argument", "action=neighbors 时必须提供 assetId。");
                var neighbors = await GetNeighbors(assetId, depth, relationTypes, cancellationToken);
                return Wrap(kind, neighbors.Summary, neighbors.Error, neighbors);
            case "path":
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                    return Fail(kind, "invalid_argument", "action=path 时必须提供 from 和 to。");
                var path = await FindPaths(from, to, maxHops, cancellationToken);
                return Wrap(kind, path.Summary, path.Error, path);
            case "impact":
                if (string.IsNullOrWhiteSpace(assetId))
                    return Fail(kind, "invalid_argument", "action=impact 时必须提供 assetId。");
                var impact = await GetImpact(assetId, maxDepth, cancellationToken);
                return Wrap(kind, impact.Summary, impact.Error, impact);
            case "explain":
                if (string.IsNullOrWhiteSpace(assetId))
                    return Fail(kind, "invalid_argument", "action=explain 时必须提供 assetId。");
                var explain = await Explain(assetId, targetAssetId, relationType, cancellationToken);
                return Wrap(kind, explain.Summary, explain.Error, explain);
            case "dependencies":
                if (string.IsNullOrWhiteSpace(assetId))
                    return Fail(kind, "invalid_argument", "action=dependencies 时必须提供 assetId。");
                var dependencies = await GetDependencies(assetId, cancellationToken);
                var dependencySummary = dependencies.Error ??
                    $"{dependencies.Asset?.Label ?? assetId}: 上游 {dependencies.Upstream.Length} / 下游 {dependencies.Downstream.Length}";
                return Wrap(kind, dependencySummary, dependencies.Error, dependencies);
            case "resolve_url":
                if (string.IsNullOrWhiteSpace(url))
                    return Fail(kind, "invalid_argument", "action=resolve_url 时必须提供 url。");
                var resolution = await ResolveUrl(url, cancellationToken);
                return Wrap(kind, resolution.Summary, resolution.Error, resolution);
            default:
                return Fail(kind, "invalid_argument", "action 须为 overview / search / neighbors / path / impact / explain / dependencies / resolve_url");
        }
    }

    private async Task<GraphSearchResult> Search(
        [Description("搜索词: 名称、ID、主机、端口或标签片段")] string query,
        [Description("可选类型过滤: ssh,datasource,application,domain; 留空=全部")] string? kinds = null,
        [Description("返回上限, 默认20, 最大50")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _query.SearchAsync(query, Split(kinds), limit, cancellationToken);
        await LogAuditAsync($"TOPOLOGY_SEARCH: {query}", result.Summary, result.Error is null);
        return result;
    }

    private async Task<GraphNeighborhood> GetNeighbors(
        [Description("资产ID, 如 ssh:web-01 / ds:mysql-01 / app:order-service, 也接受纯ID或名称")] string assetId,
        [Description("跳数, 默认1, 最大6")] int depth = 1,
        [Description("可选关系类型过滤, 如 runsOn,runsIn,connectsTo,canAccess,upstreamOf,accessedVia,relatedTo; 留空=全部")] string? relationTypes = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _query.GetNeighborsAsync(assetId, depth, Split(relationTypes), cancellationToken);
        await LogAuditAsync($"TOPOLOGY_NEIGHBORS: {assetId}", result.Summary, result.Error is null);
        return result;
    }

    private async Task<GraphPathResult> FindPaths(
        [Description("起点资产ID/名称")] string from,
        [Description("终点资产ID/名称")] string to,
        [Description("最大跳数, 默认6, 最大6")] int maxHops = 6,
        CancellationToken cancellationToken = default)
    {
        var result = await _query.FindPathsAsync(from, to, maxHops, cancellationToken);
        await LogAuditAsync($"TOPOLOGY_PATH: {from} -> {to}", result.Summary, result.Error is null);
        return result;
    }

    private async Task<GraphImpactResult> GetImpact(
        [Description("资产ID, 如 ssh:web-01 / ds:mysql-01 / app:order-service, 也接受纯ID或名称")] string assetId,
        [Description("最大反向跳数, 默认6, 最大6")] int maxDepth = 6,
        CancellationToken cancellationToken = default)
    {
        var result = await _query.GetImpactAsync(assetId, maxDepth, false, cancellationToken);
        await LogAuditAsync($"TOPOLOGY_IMPACT: {assetId}", result.Summary, result.Error is null);
        return result;
    }

    private async Task<GraphExplainResult> Explain(
        [Description("资产ID/名称")] string assetId,
        [Description("可选: 只看与另一资产之间的边")] string? to = null,
        [Description("可选: 只过滤某关系类型, 如 runsOn")] string? relationType = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _query.ExplainAsync(assetId, to, relationType, cancellationToken);
        await LogAuditAsync($"TOPOLOGY_EXPLAIN: {assetId}", result.Summary, result.Error is null);
        return result;
    }

    private async Task<TopologyGraph> GetOverview()
    {
        var graph = await _topologyService.GetGraphAsync();
        await LogAuditAsync("TOPOLOGY_OVERVIEW", $"{graph.Nodes.Length} nodes / {graph.Edges.Length} edges", true);
        return graph;
    }

    private async Task<UrlResolutionResult> ResolveUrl(string url, CancellationToken cancellationToken)
    {
        var result = await _urlResolutionService.ResolveAsync(url, cancellationToken);
        await LogAuditAsync($"ASSET_RESOLVE_URL: {url}", result.Summary, result.Success);
        return result;
    }

    private async Task<DependencyGraph> GetDependencies(string assetId, CancellationToken cancellationToken)
    {
        var result = await _topologyService.GetDependenciesAsync(assetId, cancellationToken);
        await LogAuditAsync($"TOPOLOGY_DEPENDENCIES: {assetId}", result.Error, result.Error is null);
        return result;
    }

    [McpServerTool(Name = "topology_history", UseStructuredContent = true, OutputSchemaType = typeof(TopologyQueryResultDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("按服务器快照历史查看只读拓扑: action=timeline 重放最近N份快照, action=diff 比较两份快照; 数据来自快照，不是人工主图")]
    public async Task<TopologyQueryResultDto> TopologyHistory(
        [Description("查询类型: timeline / diff")] string action,
        [Description("服务器ID/名称, 如 web-01")] string serverId,
        [Description("timeline: 最近快照数量, 默认10, 最大20")] int limit = 10,
        [Description("diff: 起始快照ID, 留空=最近一次之前的快照")] long? fromSnapshotId = null,
        [Description("diff: 结束快照ID, 留空=最近一次快照")] long? toSnapshotId = null,
        CancellationToken cancellationToken = default)
    {
        var kind = action.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "timeline":
                var timeline = await _query.GetTimelineAsync(serverId, limit, cancellationToken);
                await LogAuditAsync($"TOPOLOGY_HISTORY_TIMELINE: {serverId}", timeline.Summary, timeline.Error is null);
                return Wrap(kind, timeline.Summary, timeline.Error, timeline);
            case "diff":
                var diff = await _query.GetDiffAsync(serverId, fromSnapshotId, toSnapshotId, cancellationToken);
                await LogAuditAsync($"TOPOLOGY_HISTORY_DIFF: {serverId}", diff.Summary, diff.Error is null);
                return Wrap(kind, diff.Summary, diff.Error, diff);
            default:
                return Fail(kind, "invalid_argument", "action 须为 timeline / diff");
        }
    }

    private static TopologyQueryResultDto Wrap<T>(string action, string summary, string? error, T result) => new()
    {
        Action = action,
        Success = error is null,
        Status = error is null ? null : "query_error",
        Error = error,
        Summary = summary,
        Result = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.SerializeToElement(result, ResultJson), ResultJson)
    };

    private static TopologyQueryResultDto Fail(string action, string status, string error) => new()
    {
        Action = action,
        Success = false,
        Status = status,
        Error = error
    };

    private Task LogAuditAsync(string command, string? result, bool ok) =>
        ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = "topology",
            ServerName = "topology",
            Command = command,
            Result = result,
            Status = ok ? CommandStatus.Executed : CommandStatus.Failed,
            Category = AuditCategory.Meta
        });

    private static string[]? Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var parts = value.Split(new[] { ',', ' ', ';', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }
}
