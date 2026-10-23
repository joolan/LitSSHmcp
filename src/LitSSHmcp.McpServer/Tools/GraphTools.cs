// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
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
    private readonly IAuditLogService _auditLogService;

    public GraphTools(IAssetGraphQueryService query, IAuditLogService auditLogService)
    {
        _query = query;
        _auditLogService = auditLogService;
    }

    [McpServerTool(Name = "topology_query", UseStructuredContent = true, OutputSchemaType = typeof(TopologyQueryResultDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("拓扑图统一查询入口：action=search 搜索资产、neighbors 看邻域、path 查有向链路、impact 查变更影响面、explain 解释依赖边来源/证据/过期。返回 result 为对应动作的结构化结果。")]
    public async Task<TopologyQueryResultDto> TopologyQuery(
        [Description("查询类型：search / neighbors / path / impact / explain")] string action,
        [Description("search 的搜索词：名称/ID/主机/端口/标签片段")] string? query = null,
        [Description("neighbors/impact/explain 的资产 ID/名称")] string? assetId = null,
        [Description("path 的起点资产 ID/名称")] string? from = null,
        [Description("path 的终点资产 ID/名称")] string? to = null,
        [Description("search 的类型过滤，逗号/空格分隔；留空=全部")] string? kinds = null,
        [Description("neighbors 的关系类型过滤，逗号/空格分隔；留空=全部")] string? relationTypes = null,
        [Description("explain 的关系类型过滤，如 runsOn")] string? relationType = null,
        [Description("explain 可选：只看与另一资产之间的边")] string? targetAssetId = null,
        [Description("search 返回上限，默认20，最大50")] int limit = 20,
        [Description("neighbors 跳数，默认1，最大6")] int depth = 1,
        [Description("path 最大跳数，默认6，最大6")] int maxHops = 6,
        [Description("impact 最大反向跳数，默认6，最大6")] int maxDepth = 6,
        [Description("impact 是否包含待确认(:disc:)节点，默认false")] bool includeUnconfirmed = false,
        CancellationToken cancellationToken = default)
    {
        var kind = action.Trim().ToLowerInvariant();
        switch (kind)
        {
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
                var impact = await GetImpact(assetId, maxDepth, includeUnconfirmed, cancellationToken);
                return Wrap(kind, impact.Summary, impact.Error, impact);
            case "explain":
                if (string.IsNullOrWhiteSpace(assetId))
                    return Fail(kind, "invalid_argument", "action=explain 时必须提供 assetId。");
                var explain = await Explain(assetId, targetAssetId, relationType, cancellationToken);
                return Wrap(kind, explain.Summary, explain.Error, explain);
            default:
                return Fail(kind, "invalid_argument", "action 须为 search / neighbors / path / impact / explain");
        }
    }

    [Description("按名称/ID/主机/端口/标签搜索资产(服务器/应用/服务/容器/部署/数据库/域名/端口/证书)。定位排查前先找准确切 ID")]
    private async Task<GraphSearchResult> Search(
        [Description("搜索词: 名称、ID、主机、端口或标签片段")] string query,
        [Description("可选类型过滤: ssh,datasource,application,service,container,deploy,system,domain,port,cert,mq(逗号/空格分隔); 留空=全部")] string? kinds = null,
        [Description("返回上限, 默认20, 最大50")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _query.SearchAsync(query, Split(kinds), limit, cancellationToken);
        await LogAuditAsync($"TOPOLOGY_SEARCH: {query}", result.Summary, result.Error is null);
        return result;
    }

    [Description("查询某资产的邻域子图(入边+出边, 可多跳)。看它连了谁、谁连了它")]
    private async Task<GraphNeighborhood> GetNeighbors(
        [Description("资产ID, 如 ssh:web-01 / ds:mysql-01 / app:order-service, 也接受纯ID或名称")] string assetId,
        [Description("跳数, 默认1, 最大6")] int depth = 1,
        [Description("可选关系类型过滤, 如 runsOn,connectsTo,hostedOn,listensOn,exposes,tlsBy,resolvesTo,upstreamOf,deployedFrom,partOf; 留空=全部")] string? relationTypes = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _query.GetNeighborsAsync(assetId, depth, Split(relationTypes), cancellationToken);
        await LogAuditAsync($"TOPOLOGY_NEIGHBORS: {assetId}", result.Summary, result.Error is null);
        return result;
    }

    [Description("查询两个资产之间的有向链路(如域名→应用→端口→服务器)。无路径时返回明确原因")]
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

    [Description("反向查询影响面: 某服务器/数据库/应用/证书变更会影响哪些资产(爆炸半径)。默认排除待确认节点")]
    private async Task<GraphImpactResult> GetImpact(
        [Description("资产ID, 如 ssh:web-01 / ds:mysql-01 / app:order-service, 也接受纯ID或名称")] string assetId,
        [Description("最大反向跳数, 默认6, 最大6")] int maxDepth = 6,
        [Description("是否包含待确认(:disc:)节点, 默认false")] bool includeUnconfirmed = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _query.GetImpactAsync(assetId, maxDepth, includeUnconfirmed, cancellationToken);
        await LogAuditAsync($"TOPOLOGY_IMPACT: {assetId}", result.Summary, result.Error is null);
        return result;
    }

    [Description("解释某资产的关联边: 来源(manual/discovered)、证据、末次发现时间、是否过期。用于核实 AI 的依赖结论")]
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

    [McpServerTool(Name = "topology_timeline", UseStructuredContent = true, OutputSchemaType = typeof(GraphTimelineResult), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("按服务器快照历史重放拓扑时间线: 每次快照的节点/边及相对上一次的新增、移除。回答“最近变了什么”")]
    public async Task<GraphTimelineResult> Timeline(
        [Description("服务器ID/名称, 如 web-01")] string serverId,
        [Description("最近快照数量, 默认10, 最大20")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _query.GetTimelineAsync(serverId, limit, cancellationToken);
        await LogAuditAsync($"TOPOLOGY_TIMELINE: {serverId}", result.Summary, result.Error is null);
        return result;
    }

    [McpServerTool(Name = "topology_diff", UseStructuredContent = true, OutputSchemaType = typeof(GraphDiffResult), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("比较同一服务器两份快照的拓扑差异(新增/移除节点与边); 默认比较最近两次快照")]
    public async Task<GraphDiffResult> Diff(
        [Description("服务器ID/名称, 如 web-01")] string serverId,
        [Description("可选: 起始快照ID, 留空=最近一次之前的快照")] long? fromSnapshotId = null,
        [Description("可选: 结束快照ID, 留空=最近一次快照")] long? toSnapshotId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _query.GetDiffAsync(serverId, fromSnapshotId, toSnapshotId, cancellationToken);
        await LogAuditAsync($"TOPOLOGY_DIFF: {serverId}", result.Summary, result.Error is null);
        return result;
    }

    private static TopologyQueryResultDto Wrap<T>(string action, string summary, string? error, T result) => new()
    {
        Action = action,
        Success = error is null,
        Status = error is null ? null : "query_error",
        Error = error,
        Summary = summary,
        Result = JsonSerializer.SerializeToElement(result, ResultJson)
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
