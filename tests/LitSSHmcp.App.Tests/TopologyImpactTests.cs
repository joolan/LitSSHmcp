using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TopologyImpactTests
{
    private static TopologyEdge Edge(string from, string to, string type = "connectsTo") =>
        new() { From = from, To = to, Type = type };

    [Fact]
    public void ComputeImpact_walks_incoming_edges_up_to_two_hops()
    {
        var edges = new[]
        {
            Edge("svc:b", "svc:a"),
            Edge("svc:c", "svc:b"),
            Edge("svc:d", "svc:c"),
            Edge("svc:x", "svc:other")
        };
        var visible = new HashSet<string> { "svc:a", "svc:b", "svc:c", "svc:d", "svc:x", "svc:other" };

        var (nodes, keys) = TopologyViewModel.ComputeImpact("svc:a", edges, visible);

        Assert.Equal(new[] { "svc:a", "svc:b", "svc:c" }, nodes.OrderBy(n => n));
        Assert.Equal(new[] { "svc:b|connectsTo|svc:a", "svc:c|connectsTo|svc:b" }, keys.OrderBy(k => k));
    }

    [Fact]
    public void ComputeImpact_ignores_hidden_nodes_and_respects_max_hops()
    {
        var edges = new[] { Edge("svc:b", "svc:a"), Edge("svc:c", "svc:b") };
        var visible = new HashSet<string> { "svc:a", "svc:b" };

        var (nodes, keys) = TopologyViewModel.ComputeImpact("svc:a", edges, visible);
        Assert.Equal(2, nodes.Count);
        Assert.Single(keys);

        var oneHop = TopologyViewModel.ComputeImpact("svc:a", edges, new HashSet<string> { "svc:a", "svc:b", "svc:c" }, 1);
        Assert.Equal(2, oneHop.Nodes.Count);
    }

    [Fact]
    public void ComputeImpact_missing_root_reports_only_itself()
    {
        var (nodes, keys) = TopologyViewModel.ComputeImpact(
            "svc:gone", Array.Empty<TopologyEdge>(), new HashSet<string> { "svc:a" });

        Assert.Equal(new[] { "svc:gone" }, nodes);
        Assert.Empty(keys);
    }
}
