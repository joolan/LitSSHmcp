namespace LitSSHmcp.Core.Models;

public class TopologyEdge
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Evidence { get; set; }
    public string Source { get; set; } = "manual";
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>证据置信度 0..1：人工=1.0，直接观测≈0.9，推断≈0.5。</summary>
    public double Confidence { get; set; } = 0.8;

    /// <summary>冲突标记：人工关系与发现结果不一致时由上层置位。</summary>
    public bool Disputed { get; set; }

    /// <summary>冲突原因（自动检测写入，用于 explain/UI）。</summary>
    public string? DisputeReason { get; set; }

    /// <summary>人工已确认该冲突可接受；冲突仍存在时不再重复标红。</summary>
    public bool DisputeAcknowledged { get; set; }

    /// <summary>事实生效时间（可选）。</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>事实失效时间（可选）。</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>最近一次被证据确认的时间（stale 判定优先使用）。</summary>
    public DateTime? LastVerifiedAt { get; set; }
}

public class TopologyNode
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
    public Dictionary<string, object?> Info { get; set; } = new();
}

public class TopologyGraph
{
    public TopologyNode[] Nodes { get; set; } = Array.Empty<TopologyNode>();
    public TopologyEdge[] Edges { get; set; } = Array.Empty<TopologyEdge>();
    public Dictionary<string, object?> Summary { get; set; } = new();
}

public class DependencyGraph
{
    public TopologyNode? Asset { get; set; }
    public TopologyNode[] Upstream { get; set; } = Array.Empty<TopologyNode>();
    public TopologyNode[] Downstream { get; set; } = Array.Empty<TopologyNode>();
    public TopologyEdge[] Edges { get; set; } = Array.Empty<TopologyEdge>();
    public string? Error { get; set; }
}

public class DiscoveredApp
{
    public string SshNode { get; set; } = string.Empty;
    public long Pid { get; set; }
    public string User { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? JarPath { get; set; }
    public bool MatchedConfiguredApp { get; set; }
}

public class DiscoveredContainer
{
    public string SshNode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string Ports { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool MatchedConfiguredApp { get; set; }
}

public class DuplicateAssetItem
{
    public string AssetType { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Detail { get; set; }
}

public class DuplicateAssetGroup
{
    public string AssetType { get; set; } = string.Empty;
    public string MatchKey { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DuplicateAssetItem[] Items { get; set; } = Array.Empty<DuplicateAssetItem>();
}

public class DiscoveryResult
{
    public string[] ScannedServers { get; set; } = Array.Empty<string>();
    public TopologyEdge[] NewEdges { get; set; } = Array.Empty<TopologyEdge>();
    public TopologyEdge[] UpdatedEdges { get; set; } = Array.Empty<TopologyEdge>();
    public DiscoveredApp[] JavaProcesses { get; set; } = Array.Empty<DiscoveredApp>();
    public DiscoveredContainer[] DockerContainers { get; set; } = Array.Empty<DiscoveredContainer>();
    public List<Dictionary<string, object?>> UnmatchedMysqlClients { get; set; } = new();
    public List<Dictionary<string, object?>> UnmatchedEndpoints { get; set; } = new();
    public List<DuplicateAssetGroup> DuplicateCandidates { get; set; } = new();
    public int IdentityCount { get; set; }
    public List<string> IdentityConflicts { get; set; } = new();
    public string[] Errors { get; set; } = Array.Empty<string>();
    public string[] Notes { get; set; } = Array.Empty<string>();
}
