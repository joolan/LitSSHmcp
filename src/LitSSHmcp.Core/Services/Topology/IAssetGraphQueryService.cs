using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Topology;

/// <summary>
/// 资产图谱查询（P0）：在现有 Topology 图上做搜索、邻域、路径、影响面与证据解释。
/// 只读、无副作用；不改变 <see cref="ITopologyService"/> 的既有行为。
/// </summary>
public interface IAssetGraphQueryService
{
    Task<GraphSearchResult> SearchAsync(
        string query, string[]? kinds = null, int limit = 20, CancellationToken ct = default);

    Task<GraphNeighborhood> GetNeighborsAsync(
        string assetId, int depth = 1, string[]? relationTypes = null, CancellationToken ct = default);

    Task<GraphPathResult> FindPathsAsync(
        string fromId, string toId, int maxHops = 6, CancellationToken ct = default);

    Task<GraphImpactResult> GetImpactAsync(
        string assetId, int maxDepth = 6, bool includeUnconfirmed = false, CancellationToken ct = default);

    Task<GraphExplainResult> ExplainAsync(
        string assetId, string? toId = null, string? relationType = null, CancellationToken ct = default);

    Task<GraphTimelineResult> GetTimelineAsync(
        string serverId, int limit = 10, CancellationToken ct = default);

    Task<GraphDiffResult> GetDiffAsync(
        string serverId, long? fromSnapshotId = null, long? toSnapshotId = null, CancellationToken ct = default);
}

/// <summary>基于 <see cref="ITopologyService.GetGraphAsync"/> 的内存图查询实现（P0 不引入新表/新存储）。</summary>
public sealed class AssetGraphQueryService : IAssetGraphQueryService
{
    private const int MaxDepth = 6;
    private const int MaxResultNodes = 200;
    private const int MaxPaths = 5;
    private const int MaxSearchLimit = 50;
    private const int MaxPathExpansions = 5000;
    private const int MaxDetailChars = 240;
    private const int MaxEvidenceChars = 500;
    private const int StaleDays = 7;
    private const int MaxTimelinePoints = 20;
    private const int MaxTimelineNodes = 300;

    private readonly ITopologyService _topologyService;
    private readonly ISnapshotStore? _snapshotStore;
    private readonly IConfigService? _configService;

    public AssetGraphQueryService(
        ITopologyService topologyService,
        ISnapshotStore? snapshotStore = null,
        IConfigService? configService = null)
    {
        _topologyService = topologyService;
        _snapshotStore = snapshotStore;
        _configService = configService;
    }

    public async Task<GraphSearchResult> SearchAsync(
        string query, string[]? kinds = null, int limit = 20, CancellationToken ct = default)
    {
        var q = (query ?? string.Empty).Trim();
        var result = new GraphSearchResult { Query = q };
        if (q.Length == 0)
        {
            result.Error = "query 不能为空";
            result.Summary = "请提供资产名称、ID、主机、端口或标签片段。";
            return result;
        }

        var view = await LoadAsync(ct);
        var kindSet = ParseKinds(kinds);
        var cappedLimit = Math.Clamp(limit, 1, MaxSearchLimit);

        var matches = view.Nodes.Values
            .Where(n => kindSet is null || kindSet.Contains(NormalizeKind(n.Type)))
            .Select(n => (Node: n, Score: Score(n, q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Node.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Node.Id, StringComparer.Ordinal)
            .ToList();

        result.Items = matches
            .Take(cappedLimit)
            .Select(x => new GraphSearchItem
            {
                Node = ToNodeRef(view, x.Node, includeDetail: true),
                Score = x.Score
            })
            .ToArray();
        result.Truncated = matches.Count > cappedLimit;
        result.Summary = matches.Count == 0
            ? $"未找到匹配「{q}」的资产（类型过滤：{DescribeKinds(kindSet)}）。"
            : $"匹配 {matches.Count} 个资产，返回 {result.Items.Length} 个（类型过滤：{DescribeKinds(kindSet)}）。";
        return result;
    }

    public async Task<GraphNeighborhood> GetNeighborsAsync(
        string assetId, int depth = 1, string[]? relationTypes = null, CancellationToken ct = default)
    {
        var view = await LoadAsync(ct);
        var root = Resolve(view, assetId, out var resolveError);
        var result = new GraphNeighborhood { Root = root is null ? null : ToNodeRef(view, root, includeDetail: true) };
        if (root is null)
        {
            result.Error = resolveError ?? NotFoundMessage(view, assetId);
            result.Summary = result.Error;
            return result;
        }

        var cappedDepth = Math.Clamp(depth, 1, MaxDepth);
        var typeSet = ParseRelationTypes(relationTypes);
        var visited = new Dictionary<string, int>(StringComparer.Ordinal) { [root.Id] = 0 };
        var usedEdges = new List<TopologyEdge>();
        var seenEdges = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((root.Id, 0));

        while (queue.Count > 0)
        {
            var (current, currentDepth) = queue.Dequeue();
            if (currentDepth >= cappedDepth)
                continue;

            foreach (var edge in Incident(view, current))
            {
                if (typeSet is not null && !typeSet.Contains(edge.Type))
                    continue;

                var neighbor = string.Equals(edge.From, current, StringComparison.Ordinal) ? edge.To : edge.From;
                if (!visited.TryAdd(neighbor, currentDepth + 1))
                    continue;

                queue.Enqueue((neighbor, currentDepth + 1));
                if (visited.Count > MaxResultNodes)
                {
                    visited.Remove(neighbor);
                    result.Truncated = true;
                    queue.Clear();
                    break;
                }
            }
        }

        foreach (var edge in view.Edges)
        {
            if (!visited.ContainsKey(edge.From) || !visited.ContainsKey(edge.To))
                continue;
            if (typeSet is not null && !typeSet.Contains(edge.Type))
                continue;
            var key = EdgeKey(edge);
            if (seenEdges.Add(key))
                usedEdges.Add(edge);
        }

        result.Nodes = visited.Keys
            .Where(id => !string.Equals(id, root.Id, StringComparison.Ordinal))
            .Select(id => view.Nodes[id])
            .OrderBy(n => visited[n.Id])
            .ThenBy(n => n.Type, StringComparer.Ordinal)
            .ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
            .Select(n => ToNodeRef(view, n, includeDetail: false))
            .ToArray();
        result.Edges = usedEdges.Select(ToEdgeRef).ToArray();
        result.Summary = $"「{root.Label}」{cappedDepth} 跳内找到 {result.Nodes.Length} 个节点、{result.Edges.Length} 条边"
            + (result.Truncated ? "（已截断）" : "") + "。";
        return result;
    }

    public async Task<GraphPathResult> FindPathsAsync(
        string fromId, string toId, int maxHops = 6, CancellationToken ct = default)
    {
        var view = await LoadAsync(ct);
        var from = Resolve(view, fromId, out var fromError);
        var to = Resolve(view, toId, out var toError);
        var result = new GraphPathResult
        {
            From = from is null ? null : ToNodeRef(view, from, includeDetail: false),
            To = to is null ? null : ToNodeRef(view, to, includeDetail: false)
        };

        if (from is null || to is null)
        {
            result.Error = from is null
                ? fromError ?? NotFoundMessage(view, fromId)
                : toError ?? NotFoundMessage(view, toId);
            result.Summary = result.Error;
            return result;
        }

        var cappedHops = Math.Clamp(maxHops, 1, MaxDepth);
        var paths = new List<GraphPath>();
        var nodePath = new List<string> { from.Id };
        var edgePath = new List<TopologyEdge>();
        var inPath = new HashSet<string>(StringComparer.Ordinal) { from.Id };
        var expansions = 0;
        var truncated = false;

        void Dfs(string current)
        {
            if (paths.Count >= MaxPaths || expansions++ > MaxPathExpansions)
            {
                truncated = true;
                return;
            }

            if (string.Equals(current, to.Id, StringComparison.Ordinal))
            {
                paths.Add(BuildPath(view, nodePath, edgePath));
                return;
            }

            if (edgePath.Count >= cappedHops)
                return;

            if (!view.Outgoing.TryGetValue(current, out var outgoing))
                return;

            foreach (var edge in outgoing)
            {
                if (!inPath.Add(edge.To))
                    continue;
                nodePath.Add(edge.To);
                edgePath.Add(edge);
                Dfs(edge.To);
                edgePath.RemoveAt(edgePath.Count - 1);
                nodePath.RemoveAt(nodePath.Count - 1);
                inPath.Remove(edge.To);
            }
        }

        Dfs(from.Id);

        result.Paths = paths.ToArray();
        result.Truncated = truncated;
        if (paths.Count == 0)
        {
            result.Error = $"未找到从 {from.Id} 到 {to.Id} 的有向路径（最大 {cappedHops} 跳）。"
                + "可尝试 topology_neighbors 查看邻域，或 topology_impact 反向查影响面。";
            result.Summary = result.Error;
            return result;
        }

        var shortest = paths.Min(p => p.Hops);
        result.Summary = $"找到 {paths.Count} 条有向路径（最短 {shortest} 跳，最大 {cappedHops} 跳）"
            + (truncated ? "，结果已截断" : "") + "。";
        return result;
    }

    public async Task<GraphImpactResult> GetImpactAsync(
        string assetId, int maxDepth = 6, bool includeUnconfirmed = false, CancellationToken ct = default)
    {
        var view = await LoadAsync(ct);
        var root = Resolve(view, assetId, out var resolveError);
        var result = new GraphImpactResult { Root = root is null ? null : ToNodeRef(view, root, includeDetail: true) };
        if (root is null)
        {
            result.Error = resolveError ?? NotFoundMessage(view, assetId);
            result.Summary = result.Error;
            return result;
        }

        var cappedDepth = Math.Clamp(maxDepth, 1, MaxDepth);
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        var items = new List<GraphImpactItem>();
        var queue = new Queue<(string Id, int Hops, string[] Path)>();
        queue.Enqueue((root.Id, 0, new[] { root.Id }));
        var unknown = 0;
        var truncated = false;

        while (queue.Count > 0)
        {
            var (current, hops, path) = queue.Dequeue();
            if (hops >= cappedDepth)
                continue;

            if (!view.Incoming.TryGetValue(current, out var incoming))
                continue;

            foreach (var edge in incoming)
            {
                var parent = edge.From;
                if (string.Equals(parent, root.Id, StringComparison.Ordinal) || visited.Contains(parent))
                    continue;

                var node = view.Nodes[parent];
                var unconfirmed = IsUnconfirmed(node);
                visited.Add(parent);
                if (unconfirmed && !includeUnconfirmed)
                {
                    unknown++;
                    continue;
                }

                if (items.Count >= MaxResultNodes)
                {
                    truncated = true;
                    queue.Clear();
                    break;
                }

                var nextPath = path.Append(parent).ToArray();
                items.Add(new GraphImpactItem
                {
                    Node = ToNodeRef(view, node, includeDetail: false),
                    Hops = hops + 1,
                    Via = $"{edge.From} -{edge.Type}-> {edge.To}",
                    Path = nextPath,
                    Unconfirmed = unconfirmed
                });
                queue.Enqueue((parent, hops + 1, nextPath));
            }
        }

        result.Impacted = items
            .OrderBy(i => i.Hops)
            .ThenBy(i => i.Node.Type, StringComparer.Ordinal)
            .ThenBy(i => i.Node.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        result.UnknownCount = unknown;
        result.Truncated = truncated;

        var typeCounts = result.Impacted
            .GroupBy(i => i.Node.Type, StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Count()}")
            .ToArray();
        result.Summary = result.Impacted.Length == 0
            ? $"「{root.Label}」没有已知的反向依赖（{cappedDepth} 跳内）；" +
              (unknown > 0 ? $"另有 {unknown} 个待确认节点未计入。" : "可尝试 topology_discover 补全。")
            : $"「{root.Label}」影响 {result.Impacted.Length} 个资产"
              + (typeCounts.Length > 0 ? $"（{string.Join(" / ", typeCounts)}）" : "")
              + $"，最大 {cappedDepth} 跳"
              + (unknown > 0 ? $"；另有 {unknown} 个待确认节点未计入" : "")
              + (truncated ? "；结果已截断" : "") + "。";
        return result;
    }

    public async Task<GraphExplainResult> ExplainAsync(
        string assetId, string? toId = null, string? relationType = null, CancellationToken ct = default)
    {
        var view = await LoadAsync(ct);
        var entity = Resolve(view, assetId, out var resolveError);
        var result = new GraphExplainResult { Entity = entity is null ? null : ToNodeRef(view, entity, includeDetail: true) };
        if (entity is null)
        {
            result.Error = resolveError ?? NotFoundMessage(view, assetId);
            result.Summary = result.Error;
            return result;
        }

        TopologyNode? to = null;
        if (!string.IsNullOrWhiteSpace(toId))
        {
            to = Resolve(view, toId, out var toError);
            if (to is null)
            {
                result.Error = toError ?? NotFoundMessage(view, toId);
                result.Summary = result.Error;
                return result;
            }
        }

        var type = string.IsNullOrWhiteSpace(relationType) ? null : relationType.Trim();
        var edges = view.Edges
            .Where(e => string.Equals(e.From, entity.Id, StringComparison.Ordinal)
                        || string.Equals(e.To, entity.Id, StringComparison.Ordinal))
            .Where(e => to is null
                        || string.Equals(e.From, to.Id, StringComparison.Ordinal)
                        || string.Equals(e.To, to.Id, StringComparison.Ordinal))
            .Where(e => type is null || string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => string.Equals(e.From, entity.Id, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(e => e.Type, StringComparer.Ordinal)
            .ToArray();

        result.Edges = edges.Select(ToEdgeRef).ToArray();
        var outgoing = edges.Count(e => string.Equals(e.From, entity.Id, StringComparison.Ordinal));
        var incoming = edges.Length - outgoing;
        var facts = new List<string>
        {
            $"节点类型: {entity.Type}",
            $"出边: {outgoing} / 入边: {incoming}（过滤后）",
            $"图谱总边数: {view.Edges.Count}"
        };
        if (!string.IsNullOrWhiteSpace(ToDetail(entity)))
            facts.Add($"摘要: {ToDetail(entity)}");
        var disputed = edges.Count(e => e.Disputed);
        if (disputed > 0)
            facts.Add($"冲突边: {disputed}（明细见 edges[].disputeReason，可由人工确认）");
        result.Facts = facts.ToArray();
        result.Summary = edges.Length == 0
            ? $"「{entity.Label}」没有匹配的边。"
            : $"「{entity.Label}」找到 {edges.Length} 条边（出 {outgoing} / 入 {incoming}）；来源与证据见 edges。";
        return result;
    }

    public async Task<GraphTimelineResult> GetTimelineAsync(
        string serverId, int limit = 10, CancellationToken ct = default)
    {
        var result = new GraphTimelineResult { ServerId = serverId?.Trim() };
        if (string.IsNullOrWhiteSpace(serverId))
        {
            result.Error = "serverId 不能为空";
            result.Summary = "请提供服务器 ID（如 web-01）。";
            return result;
        }
        if (_snapshotStore is null || _configService is null)
        {
            result.Error = "当前运行环境未启用快照存储";
            result.Summary = result.Error;
            return result;
        }

        var config = await _configService.LoadConfigAsync();
        var server = FindServer(config, serverId);
        if (server is null)
        {
            result.Error = $"未找到服务器: {serverId}";
            result.Summary = result.Error;
            return result;
        }

        var cappedLimit = Math.Clamp(limit, 1, MaxTimelinePoints);
        var snapshots = await _snapshotStore.GetRecentAsync(server.Id, cappedLimit, ct);
        if (snapshots.Count == 0)
        {
            result.Error = $"服务器 {server.Name} 还没有快照";
            result.Summary = result.Error;
            return result;
        }

        snapshots.Reverse();
        result.Truncated = snapshots.Count >= cappedLimit;

        Dictionary<string, TopologyNode>? previousNodes = null;
        HashSet<string>? previousEdges = null;
        foreach (var snapshot in snapshots)
        {
            ct.ThrowIfCancellationRequested();
            var (nodes, edges) = BuildSnapshotScene(config, server, snapshot);
            var nodeIds = nodes.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var edgeKeys = edges.Select(EdgeKey).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var currentEdges = edges.ToDictionary(EdgeKey, StringComparer.Ordinal);

            var point = new GraphTimelinePoint
            {
                SnapshotId = snapshot.Id,
                CapturedAt = snapshot.CompletedAt ?? snapshot.CreatedAt,
                Success = snapshot.Status == SnapshotStatus.Succeeded,
                Nodes = nodeIds.Take(MaxTimelineNodes).Select(id => ToTimelineNode(nodes[id])).ToArray(),
                Edges = edgeKeys.Take(MaxTimelineNodes).Select(k => ToEdgeRef(currentEdges[k])).ToArray()
            };

            if (previousNodes is not null && previousEdges is not null)
            {
                point.AddedNodes = nodeIds.Where(id => !previousNodes.ContainsKey(id)).ToArray();
                point.RemovedNodes = previousNodes.Keys.Where(id => !nodes.ContainsKey(id))
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
                point.AddedEdges = edgeKeys.Where(k => !previousEdges.Contains(k)).ToArray();
                point.RemovedEdges = previousEdges.Where(k => !currentEdges.ContainsKey(k))
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }

            result.Points = result.Points.Append(point).ToArray();
            previousNodes = nodes;
            previousEdges = edgeKeys.ToHashSet(StringComparer.Ordinal);
        }

        var last = result.Points[^1];
        result.Summary = $"服务器 {server.Name}: {result.Points.Length} 份快照（{result.Points[0].CapturedAt} → {last.CapturedAt}）；"
            + $"最新 {last.Nodes.Length} 节点 / {last.Edges.Length} 边，"
            + $"+{last.AddedNodes.Length} 节点 / -{last.RemovedNodes.Length} 节点。";
        return result;
    }

    public async Task<GraphDiffResult> GetDiffAsync(
        string serverId, long? fromSnapshotId = null, long? toSnapshotId = null, CancellationToken ct = default)
    {
        var result = new GraphDiffResult { ServerId = serverId?.Trim() };
        if (string.IsNullOrWhiteSpace(serverId))
        {
            result.Error = "serverId 不能为空";
            result.Summary = "请提供服务器 ID（如 web-01）。";
            return result;
        }
        if (_snapshotStore is null || _configService is null)
        {
            result.Error = "当前运行环境未启用快照存储";
            result.Summary = result.Error;
            return result;
        }

        var config = await _configService.LoadConfigAsync();
        var server = FindServer(config, serverId);
        if (server is null)
        {
            result.Error = $"未找到服务器: {serverId}";
            result.Summary = result.Error;
            return result;
        }

        var snapshots = await _snapshotStore.GetRecentAsync(server.Id, 100, ct);
        if (snapshots.Count == 0)
        {
            result.Error = $"服务器 {server.Name} 还没有快照";
            result.Summary = result.Error;
            return result;
        }

        var to = toSnapshotId is null
            ? snapshots[0]
            : snapshots.FirstOrDefault(s => s.Id == toSnapshotId.Value);
        if (to is null)
        {
            result.Error = $"未找到快照: {toSnapshotId}";
            result.Summary = result.Error;
            return result;
        }

        var from = fromSnapshotId is null
            ? snapshots.FirstOrDefault(s => s.Id != to.Id)
            : snapshots.FirstOrDefault(s => s.Id == fromSnapshotId.Value);
        if (from is null)
        {
            result.Error = fromSnapshotId is null
                ? "至少需要两份快照才能比较"
                : $"未找到快照: {fromSnapshotId}";
            result.Summary = result.Error;
            return result;
        }
        if (string.CompareOrdinal(from.CreatedAt, to.CreatedAt) > 0)
        {
            result.Error = "from 快照必须早于 to 快照";
            result.Summary = result.Error;
            return result;
        }

        var (fromNodes, fromEdges) = BuildSnapshotScene(config, server, from);
        var (toNodes, toEdges) = BuildSnapshotScene(config, server, to);
        var fromEdgeMap = fromEdges.ToDictionary(EdgeKey, StringComparer.Ordinal);
        var toEdgeMap = toEdges.ToDictionary(EdgeKey, StringComparer.Ordinal);

        var addedNodeIds = toNodes.Keys.Where(id => !fromNodes.ContainsKey(id))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var removedNodeIds = fromNodes.Keys.Where(id => !toNodes.ContainsKey(id))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var addedEdgeKeys = toEdgeMap.Keys.Where(k => !fromEdgeMap.ContainsKey(k))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var removedEdgeKeys = fromEdgeMap.Keys.Where(k => !toEdgeMap.ContainsKey(k))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();

        result.FromSnapshot = $"{from.Id}@{from.CreatedAt}";
        result.ToSnapshot = $"{to.Id}@{to.CreatedAt}";
        result.AddedNodes = addedNodeIds.Take(MaxTimelineNodes).Select(id => ToTimelineNode(toNodes[id])).ToArray();
        result.RemovedNodes = removedNodeIds.Take(MaxTimelineNodes).Select(id => ToTimelineNode(fromNodes[id])).ToArray();
        result.AddedEdges = addedEdgeKeys.Take(MaxTimelineNodes).Select(k => ToEdgeRef(toEdgeMap[k])).ToArray();
        result.RemovedEdges = removedEdgeKeys.Take(MaxTimelineNodes).Select(k => ToEdgeRef(fromEdgeMap[k])).ToArray();
        result.Truncated = addedNodeIds.Length > MaxTimelineNodes || removedNodeIds.Length > MaxTimelineNodes
            || addedEdgeKeys.Length > MaxTimelineNodes || removedEdgeKeys.Length > MaxTimelineNodes;

        result.Summary = $"服务器 {server.Name}: 快照 {from.Id} → {to.Id}；"
            + $"+{addedNodeIds.Length} 节点 / -{removedNodeIds.Length} 节点，"
            + $"+{addedEdgeKeys.Length} 边 / -{removedEdgeKeys.Length} 边。";
        return result;
    }

    private static SshServerConfig? FindServer(AppConfig config, string serverId) =>
        config.Servers.FirstOrDefault(s =>
            string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Name, serverId, StringComparison.OrdinalIgnoreCase));

    private static (Dictionary<string, TopologyNode> Nodes, List<TopologyEdge> Edges) BuildSnapshotScene(
        AppConfig config, SshServerConfig server, ServerSnapshot snapshot)
    {
        var contribution = SnapshotTopologyBuilder.Build(config, server, snapshot);
        var nodes = new Dictionary<string, TopologyNode>(StringComparer.Ordinal);

        void Ensure(string id)
        {
            if (!nodes.ContainsKey(id))
                nodes[id] = new TopologyNode { Id = id, Type = TypeFromId(id), Label = id };
        }

        foreach (var edge in contribution.Edges)
        {
            Ensure(edge.From);
            Ensure(edge.To);
        }
        foreach (var (id, info) in contribution.NodeInfo)
        {
            Ensure(id);
            if (info.TryGetValue("label", out var label) && label is string text && !string.IsNullOrWhiteSpace(text))
                nodes[id].Label = text;
        }

        return (nodes, contribution.Edges);
    }

    private static string TypeFromId(string id)
    {
        var index = id.IndexOf(':');
        return (index > 0 ? id[..index] : id).ToLowerInvariant() switch
        {
            "ssh" => "ssh",
            "ds" => "datasource",
            "app" => "application",
            "mq" => "mq",
            _ => (index > 0 ? id[..index] : id).ToLowerInvariant()
        };
    }

    private static GraphNodeRef ToTimelineNode(TopologyNode node) => new()
    {
        Id = node.Id,
        Label = node.Label,
        Type = node.Type
    };

    private async Task<GraphView> LoadAsync(CancellationToken ct)
    {
        var graph = await _topologyService.GetGraphAsync(ct);
        return new GraphView(graph);
    }

    private static GraphPath BuildPath(GraphView view, IReadOnlyList<string> nodeIds, IReadOnlyList<TopologyEdge> edges)
    {
        var nodes = nodeIds.Select(id => ToNodeRef(view, view.Nodes[id], includeDetail: false)).ToArray();
        var edgeRefs = edges.Select(ToEdgeRef).ToArray();
        return new GraphPath
        {
            Nodes = nodes,
            Edges = edgeRefs,
            Hops = edges.Count,
            Complete = true,
            Summary = string.Join(" -> ", nodes.Select(n => n.Label))
        };
    }

    private static IEnumerable<TopologyEdge> Incident(GraphView view, string nodeId)
    {
        if (view.Outgoing.TryGetValue(nodeId, out var outgoing))
            foreach (var edge in outgoing)
                yield return edge;
        if (view.Incoming.TryGetValue(nodeId, out var incoming))
            foreach (var edge in incoming)
                yield return edge;
    }

    private static TopologyNode? Resolve(GraphView view, string? assetId, out string? error)
    {
        error = null;
        var id = (assetId ?? string.Empty).Trim();
        if (id.Length == 0)
        {
            error = "asset_id_required: 资产 ID 不能为空。";
            return null;
        }

        if (view.Nodes.TryGetValue(id, out var exact))
            return exact;

        var byId = view.Nodes.Values
            .Where(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (byId.Length == 1)
            return byId[0];
        if (byId.Length > 1)
        {
            error = AmbiguousMessage(id, byId);
            return null;
        }

        var bySuffix = view.Nodes.Values
            .Where(n => n.Id.EndsWith(":" + id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (bySuffix.Length == 1)
            return bySuffix[0];
        if (bySuffix.Length > 1)
        {
            error = AmbiguousMessage(id, bySuffix);
            return null;
        }

        var byLabel = view.Nodes.Values
            .Where(n => string.Equals(n.Label, id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (byLabel.Length == 1)
            return byLabel[0];
        if (byLabel.Length > 1)
        {
            error = AmbiguousMessage(id, byLabel);
            return null;
        }

        return null;
    }

    private static string AmbiguousMessage(string input, IEnumerable<TopologyNode> matches)
    {
        var candidates = matches
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Take(10)
            .Select(n => $"{n.Id}（{n.Label}）");
        return $"asset_ambiguous: 资产标识「{input}」匹配多个资产，请改用精确 ID: {string.Join(", ", candidates)}";
    }

    private static string NotFoundMessage(GraphView view, string? assetId)
    {
        var sample = view.Nodes.Values
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Take(10)
            .Select(n => n.Id)
            .ToArray();
        return $"未找到资产「{assetId}」。可用 ID 示例: {string.Join(", ", sample)}"
            + (sample.Length == 0 ? "（当前图谱为空）" : "");
    }

    private static int Score(TopologyNode node, string query)
    {
        if (node.Id.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (node.Label.Equals(query, StringComparison.OrdinalIgnoreCase)) return 900;
        if (node.Id.EndsWith(":" + query, StringComparison.OrdinalIgnoreCase)) return 800;
        if (node.Label.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 700;
        if (node.Id.Contains(query, StringComparison.OrdinalIgnoreCase)) return 500;
        if (node.Label.Contains(query, StringComparison.OrdinalIgnoreCase)) return 400;
        if (InfoText(node).Contains(query, StringComparison.OrdinalIgnoreCase)) return 200;
        return 0;
    }

    private static GraphNodeRef ToNodeRef(GraphView view, TopologyNode node, bool includeDetail) => new()
    {
        Id = node.Id,
        Label = node.Label,
        Type = node.Type,
        Degree = view.Degree(node.Id),
        Detail = includeDetail ? ToDetail(node) : null
    };

    private static GraphEdgeRef ToEdgeRef(TopologyEdge edge)
    {
        var verifiedAt = edge.LastVerifiedAt ?? edge.LastSeenAt;
        var stale = string.Equals(edge.Source, "discovered", StringComparison.OrdinalIgnoreCase)
                    && verifiedAt != default
                    && (DateTime.UtcNow - verifiedAt).TotalDays > StaleDays;
        return new GraphEdgeRef
        {
            From = edge.From,
            To = edge.To,
            Type = edge.Type,
            Source = edge.Source,
            Evidence = Truncate(edge.Evidence, MaxEvidenceChars),
            Confidence = edge.Confidence,
            Disputed = edge.Disputed,
            DisputeReason = Truncate(edge.DisputeReason, MaxEvidenceChars),
            DisputeAcknowledged = edge.DisputeAcknowledged,
            ValidFrom = edge.ValidFrom?.ToString("O"),
            ValidTo = edge.ValidTo?.ToString("O"),
            LastSeenAt = edge.LastSeenAt == default ? null : edge.LastSeenAt.ToString("O"),
            LastVerifiedAt = edge.LastVerifiedAt?.ToString("O"),
            Stale = stale
        };
    }

    private static string? ToDetail(TopologyNode node)
    {
        var parts = new List<string>();
        var host = InfoValue(node, "host");
        var port = InfoValue(node, "port");
        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(port))
            parts.Add($"{host}:{port}");
        else if (!string.IsNullOrWhiteSpace(host))
            parts.Add(host!);
        else if (!string.IsNullOrWhiteSpace(port))
            parts.Add($":{port}");

        var kind = InfoValue(node, "datasourceType")
                   ?? InfoValue(node, "applicationType")
                   ?? InfoValue(node, "unit")
                   ?? InfoValue(node, "image");
        if (!string.IsNullOrWhiteSpace(kind))
            parts.Add(kind!);

        var state = InfoValue(node, "state") ?? InfoValue(node, "active");
        if (!string.IsNullOrWhiteSpace(state))
            parts.Add(state!);

        var tags = InfoValue(node, "tags");
        if (!string.IsNullOrWhiteSpace(tags) && tags != "[]")
            parts.Add("tags=" + tags);

        var description = InfoValue(node, "description");
        if (!string.IsNullOrWhiteSpace(description))
            parts.Add(description!);

        return parts.Count == 0 ? null : Truncate(string.Join(" | ", parts), MaxDetailChars);
    }

    private static string InfoText(TopologyNode node) =>
        node.Info is null
            ? string.Empty
            : string.Join(" ", node.Info.Select(kv => $"{kv.Key}={FormatValue(kv.Value)}"));

    private static string? InfoValue(TopologyNode node, string key) =>
        node.Info is not null && node.Info.TryGetValue(key, out var value) ? FormatValue(value) : null;

    private static string FormatValue(object? value)
    {
        if (value is null)
            return string.Empty;
        if (value is string s)
            return s;
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Join(",", element.EnumerateArray().Select(x => x.ToString())),
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => element.ToString()
            };
        }
        if (value is IEnumerable<string> strings)
            return string.Join(",", strings);
        return value.ToString() ?? string.Empty;
    }

    private static bool IsUnconfirmed(TopologyNode node) =>
        node.Id.Contains(":disc:", StringComparison.Ordinal)
        || string.Equals(node.Type, "unknown", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string>? ParseKinds(string[]? kinds)
    {
        var values = SplitValues(kinds);
        if (values.Count == 0)
            return null;
        return values.Select(NormalizeKind).ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string>? ParseRelationTypes(string[]? relationTypes)
    {
        var values = SplitValues(relationTypes);
        return values.Count == 0 ? null : values.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> SplitValues(string[]? values) =>
        (values ?? Array.Empty<string>())
            .SelectMany(v => (v ?? string.Empty).Split(new[] { ',', ' ', ';', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(v => v.Length > 0)
            .ToList();

    private static string NormalizeKind(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "ssh" or "server" => "ssh",
        "ds" or "datasource" or "database" or "db" => "datasource",
        "app" or "application" => "application",
        "service" or "systemd" => "service",
        "container" or "docker" => "container",
        "deploy" or "deployment" or "release" => "deploy",
        "system" or "bizsystem" => "system",
        "domain" or "host" or "hostname" or "site" => "domain",
        "port" or "ports" or "listener" => "port",
        "cert" or "certificate" or "tls" => "cert",
        "mq" or "queue" or "messagequeue" => "mq",
        var other => other
    };

    private static string DescribeKinds(HashSet<string>? kinds) =>
        kinds is null ? "全部" : string.Join("/", kinds.OrderBy(k => k, StringComparer.Ordinal));

    private static string EdgeKey(TopologyEdge edge) => $"{edge.From}|{edge.To}|{edge.Type}";

    private static string? Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text;
        return text[..Math.Max(0, maxChars - 1)] + "…";
    }

    private sealed class GraphView
    {
        public Dictionary<string, TopologyNode> Nodes { get; }
        public List<TopologyEdge> Edges { get; }
        public Dictionary<string, List<TopologyEdge>> Outgoing { get; }
        public Dictionary<string, List<TopologyEdge>> Incoming { get; }

        public GraphView(TopologyGraph graph)
        {
            Nodes = (graph.Nodes ?? Array.Empty<TopologyNode>())
                .Where(n => n is not null && !string.IsNullOrWhiteSpace(n.Id))
                .GroupBy(n => n.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            Edges = (graph.Edges ?? Array.Empty<TopologyEdge>())
                .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.From) && !string.IsNullOrWhiteSpace(e.To))
                .ToList();

            foreach (var edge in Edges)
            {
                Nodes.TryAdd(edge.From, Placeholder(edge.From));
                Nodes.TryAdd(edge.To, Placeholder(edge.To));
            }

            Outgoing = new Dictionary<string, List<TopologyEdge>>(StringComparer.Ordinal);
            Incoming = new Dictionary<string, List<TopologyEdge>>(StringComparer.Ordinal);
            foreach (var edge in Edges)
            {
                Add(Outgoing, edge.From, edge);
                Add(Incoming, edge.To, edge);
            }
        }

        public int Degree(string id) =>
            (Outgoing.TryGetValue(id, out var o) ? o.Count : 0)
            + (Incoming.TryGetValue(id, out var i) ? i.Count : 0);

        private static void Add(Dictionary<string, List<TopologyEdge>> map, string key, TopologyEdge edge)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<TopologyEdge>();
            list.Add(edge);
        }

        private static TopologyNode Placeholder(string id) => new()
        {
            Id = id,
            Label = id,
            Type = "unknown"
        };
    }
}
