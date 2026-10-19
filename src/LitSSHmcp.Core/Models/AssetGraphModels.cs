namespace LitSSHmcp.Core.Models;

/// <summary>图查询返回的节点引用（P0：不暴露完整 Info，避免上下文膨胀）。</summary>
public sealed class GraphNodeRef
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public int Degree { get; set; }
}

/// <summary>图查询返回的边引用（含来源/证据/末次发现，便于解释结论）。</summary>
public sealed class GraphEdgeRef
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Evidence { get; set; }
    public double Confidence { get; set; }
    public bool Disputed { get; set; }
    public string? DisputeReason { get; set; }
    public bool DisputeAcknowledged { get; set; }
    public string? ValidFrom { get; set; }
    public string? ValidTo { get; set; }
    public string? LastSeenAt { get; set; }
    public string? LastVerifiedAt { get; set; }
    public bool Stale { get; set; }
}

public sealed class GraphSearchItem
{
    public GraphNodeRef Node { get; set; } = new();
    public int Score { get; set; }
}

public sealed class GraphSearchResult
{
    public string Query { get; set; } = string.Empty;
    public GraphSearchItem[] Items { get; set; } = Array.Empty<GraphSearchItem>();
    public bool Truncated { get; set; }
    public string? Error { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class GraphNeighborhood
{
    public GraphNodeRef? Root { get; set; }
    public GraphNodeRef[] Nodes { get; set; } = Array.Empty<GraphNodeRef>();
    public GraphEdgeRef[] Edges { get; set; } = Array.Empty<GraphEdgeRef>();
    public bool Truncated { get; set; }
    public string? Error { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class GraphPath
{
    public GraphNodeRef[] Nodes { get; set; } = Array.Empty<GraphNodeRef>();
    public GraphEdgeRef[] Edges { get; set; } = Array.Empty<GraphEdgeRef>();
    public int Hops { get; set; }
    public bool Complete { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class GraphPathResult
{
    public GraphNodeRef? From { get; set; }
    public GraphNodeRef? To { get; set; }
    public GraphPath[] Paths { get; set; } = Array.Empty<GraphPath>();
    public bool Truncated { get; set; }
    public string? Error { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class GraphImpactItem
{
    public GraphNodeRef Node { get; set; } = new();
    public int Hops { get; set; }

    /// <summary>反向遍历经过的边（按依赖方向描述，如 app -runsOn-> ssh）。</summary>
    public string Via { get; set; } = string.Empty;

    /// <summary>从根到该资产的节点 ID 路径（反向遍历顺序）。</summary>
    public string[] Path { get; set; } = Array.Empty<string>();

    public bool Unconfirmed { get; set; }
}

public sealed class GraphImpactResult
{
    public GraphNodeRef? Root { get; set; }
    public GraphImpactItem[] Impacted { get; set; } = Array.Empty<GraphImpactItem>();
    public int UnknownCount { get; set; }
    public bool Truncated { get; set; }
    public string? Error { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class GraphExplainResult
{
    public GraphNodeRef? Entity { get; set; }
    public GraphEdgeRef[] Edges { get; set; } = Array.Empty<GraphEdgeRef>();
    public string[] Facts { get; set; } = Array.Empty<string>();
    public string? Error { get; set; }
    public string Summary { get; set; } = string.Empty;
}

/// <summary>时间线上的一个快照点：该次快照重放出的节点/边，以及与上一点的增量。</summary>
public sealed class GraphTimelinePoint
{
    public long SnapshotId { get; set; }
    public string? CapturedAt { get; set; }
    public bool Success { get; set; }
    public GraphNodeRef[] Nodes { get; set; } = Array.Empty<GraphNodeRef>();
    public GraphEdgeRef[] Edges { get; set; } = Array.Empty<GraphEdgeRef>();
    public string[] AddedNodes { get; set; } = Array.Empty<string>();
    public string[] RemovedNodes { get; set; } = Array.Empty<string>();
    public string[] AddedEdges { get; set; } = Array.Empty<string>();
    public string[] RemovedEdges { get; set; } = Array.Empty<string>();
}

/// <summary>按快照历史重放出的拓扑时间线（用于回答“这台机器最近变了什么”）。</summary>
public sealed class GraphTimelineResult
{
    public string? ServerId { get; set; }
    public GraphTimelinePoint[] Points { get; set; } = Array.Empty<GraphTimelinePoint>();
    public bool Truncated { get; set; }
    public string? Error { get; set; }
    public string Summary { get; set; } = string.Empty;
}

/// <summary>两个快照之间（或最新两次之间）的拓扑差异。</summary>
public sealed class GraphDiffResult
{
    public string? ServerId { get; set; }
    public string? FromSnapshot { get; set; }
    public string? ToSnapshot { get; set; }
    public GraphNodeRef[] AddedNodes { get; set; } = Array.Empty<GraphNodeRef>();
    public GraphNodeRef[] RemovedNodes { get; set; } = Array.Empty<GraphNodeRef>();
    public GraphEdgeRef[] AddedEdges { get; set; } = Array.Empty<GraphEdgeRef>();
    public GraphEdgeRef[] RemovedEdges { get; set; } = Array.Empty<GraphEdgeRef>();
    public bool Truncated { get; set; }
    public string? Error { get; set; }
    public string Summary { get; set; } = string.Empty;
}
