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

public class DiscoveryResult
{
    public string[] ScannedServers { get; set; } = Array.Empty<string>();
    public TopologyEdge[] NewEdges { get; set; } = Array.Empty<TopologyEdge>();
    public TopologyEdge[] UpdatedEdges { get; set; } = Array.Empty<TopologyEdge>();
    public DiscoveredApp[] JavaProcesses { get; set; } = Array.Empty<DiscoveredApp>();
    public DiscoveredContainer[] DockerContainers { get; set; } = Array.Empty<DiscoveredContainer>();
    public List<Dictionary<string, object?>> UnmatchedMysqlClients { get; set; } = new();
    public List<Dictionary<string, object?>> UnmatchedEndpoints { get; set; } = new();
    public string[] Errors { get; set; } = Array.Empty<string>();
    public string[] Notes { get; set; } = Array.Empty<string>();
}
