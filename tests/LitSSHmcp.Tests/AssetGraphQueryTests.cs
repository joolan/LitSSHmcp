using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>P0 资产图查询：搜索/邻域/路径/影响面/证据解释（不依赖数据库与远端）。</summary>
public class AssetGraphQueryTests
{
    private static AssetGraphQueryService CreateService() => new(new FakeTopologyService(BuildGraph()));

    [Fact]
    public async Task Search_matches_id_label_host_and_filters_by_kind()
    {
        var service = CreateService();

        var byLabel = await service.SearchAsync("订单");
        Assert.Contains(byLabel.Items, i => i.Node.Id == "ds:order-db");

        var byHost = await service.SearchAsync("10.0.0.20");
        Assert.Contains(byHost.Items, i => i.Node.Id == "ds:order-db");
        Assert.Contains("mysql", byHost.Items.First(i => i.Node.Id == "ds:order-db").Node.Detail ?? string.Empty);

        var onlySsh = await service.SearchAsync("10.0.0.10", new[] { "ssh" });
        Assert.All(onlySsh.Items, i => Assert.Equal("ssh", i.Node.Type));
        Assert.Contains(onlySsh.Items, i => i.Node.Id == "ssh:web-01");
    }

    [Fact]
    public async Task Search_supports_domain_port_and_certificate_kinds()
    {
        var service = CreateService();

        var domain = await service.SearchAsync("shop.example.com", new[] { "domain" });
        Assert.Contains(domain.Items, i => i.Node.Id == "domain:shop.example.com" && i.Node.Type == "domain");

        var cert = await service.SearchAsync("2027-01-01", new[] { "certificate" });
        Assert.Contains(cert.Items, i => i.Node.Id == "cert:abc123" && i.Node.Type == "cert");

        var port = await service.SearchAsync("443", new[] { "port" });
        Assert.Contains(port.Items, i => i.Node.Id == "port:web-01:tcp-443" && i.Node.Type == "port");
    }

    [Fact]
    public async Task Search_supports_service_container_and_deploy_kinds()
    {
        var graph = BuildGraph();
        graph.Nodes = graph.Nodes.Concat(new[]
        {
            new TopologyNode
            {
                Id = "service:s1:nginx", Label = "nginx.service", Type = "service",
                Info = new() { ["unit"] = "nginx.service", ["active"] = "active" }
            },
            new TopologyNode
            {
                Id = "container:s1:shop-web", Label = "shop-web", Type = "container",
                Info = new() { ["image"] = "registry.local/shop:1.4.2", ["state"] = "running" }
            },
            new TopologyNode
            {
                Id = "deploy:shop@1.4.2", Label = "shop@1.4.2", Type = "deploy",
                Info = new() { ["version"] = "1.4.2", ["image"] = "registry.local/shop:1.4.2" }
            }
        }).ToArray();
        var service = new AssetGraphQueryService(new FakeTopologyService(graph));

        var serviceNode = await service.SearchAsync("nginx.service", new[] { "service" });
        Assert.Contains(serviceNode.Items, i => i.Node.Id == "service:s1:nginx");

        var container = await service.SearchAsync("shop-web", new[] { "container" });
        Assert.Contains(container.Items, i => i.Node.Id == "container:s1:shop-web");

        var deploy = await service.SearchAsync("1.4.2", new[] { "deployment" });
        Assert.Contains(deploy.Items, i => i.Node.Id == "deploy:shop@1.4.2");
    }

    [Fact]
    public async Task Search_returns_empty_with_hint_for_unknown_query()
    {
        var service = CreateService();

        var result = await service.SearchAsync("no-such-asset");

        Assert.Empty(result.Items);
        Assert.Null(result.Error);
        Assert.Contains("未找到", result.Summary);
    }

    [Fact]
    public async Task Path_finds_directed_chain_and_reports_missing_direction()
    {
        var service = CreateService();

        var forward = await service.FindPathsAsync("domain:shop.example.com", "ds:order-db");
        Assert.Null(forward.Error);
        Assert.Single(forward.Paths);
        Assert.Equal(2, forward.Paths[0].Hops);
        Assert.Equal(new[] { "domain:shop.example.com", "app:order-service", "ds:order-db" },
            forward.Paths[0].Nodes.Select(n => n.Id).ToArray());

        var reverse = await service.FindPathsAsync("ds:order-db", "domain:shop.example.com");
        Assert.NotNull(reverse.Error);
        Assert.Empty(reverse.Paths);
        Assert.Contains("有向路径", reverse.Error);
    }

    [Fact]
    public async Task Path_is_cycle_safe_and_depth_limited()
    {
        var graph = BuildGraph();
        graph.Edges = graph.Edges.Concat(new[]
        {
            new TopologyEdge { From = "app:order-service", To = "app:batch", Type = "relatedTo", Source = "manual" },
            new TopologyEdge { From = "app:batch", To = "app:order-service", Type = "relatedTo", Source = "manual" }
        }).ToArray();
        var service = new AssetGraphQueryService(new FakeTopologyService(graph));

        var result = await service.FindPathsAsync("app:order-service", "app:order-service", maxHops: 6);

        Assert.Single(result.Paths);
        Assert.Equal(0, result.Paths[0].Hops);

        var limited = await service.FindPathsAsync("domain:shop.example.com", "ssh:web-01", maxHops: 1);
        Assert.NotNull(limited.Error);
    }

    [Fact]
    public async Task Neighbors_returns_in_and_out_edges_with_depth()
    {
        var service = CreateService();

        var result = await service.GetNeighborsAsync("ssh:web-01", depth: 1);
        Assert.Null(result.Error);
        Assert.Contains(result.Nodes, n => n.Id == "app:order-service");
        Assert.Contains(result.Nodes, n => n.Id == "app:batch");
        Assert.DoesNotContain(result.Nodes, n => n.Id == "domain:shop.example.com");
        Assert.Contains(result.Edges, e => e.From == "app:order-service" && e.To == "ssh:web-01");

        var deeper = await service.GetNeighborsAsync("ssh:web-01", depth: 2);
        Assert.Contains(deeper.Nodes, n => n.Id == "domain:shop.example.com");
    }

    [Fact]
    public async Task Impact_reverse_traverses_dependencies_and_excludes_unconfirmed_by_default()
    {
        var service = CreateService();

        var result = await service.GetImpactAsync("ssh:web-01");
        Assert.Null(result.Error);
        Assert.Equal(3, result.Impacted.Length);
        Assert.Contains(result.Impacted, i => i.Node.Id == "app:order-service" && i.Hops == 1);
        Assert.Contains(result.Impacted, i => i.Node.Id == "domain:shop.example.com" && i.Hops == 2);
        Assert.DoesNotContain(result.Impacted, i => i.Unconfirmed);
        Assert.Equal(1, result.UnknownCount);
        Assert.Contains("待确认", result.Summary);

        var withUnconfirmed = await service.GetImpactAsync("ssh:web-01", includeUnconfirmed: true);
        Assert.Equal(4, withUnconfirmed.Impacted.Length);
        Assert.Contains(withUnconfirmed.Impacted, i => i.Node.Id == "app:disc:foo" && i.Unconfirmed);
        Assert.Equal(0, withUnconfirmed.UnknownCount);
    }

    [Fact]
    public async Task Explain_exposes_source_evidence_and_filters()
    {
        var service = CreateService();

        var all = await service.ExplainAsync("ssh:web-01");
        Assert.Null(all.Error);
        Assert.Contains(all.Edges, e => e.From == "app:order-service" && e.Type == "runsOn");
        Assert.Contains(all.Facts, f => f.Contains("节点类型: ssh", StringComparison.Ordinal));

        var filtered = await service.ExplainAsync("ssh:web-01", toId: "app:order-service", relationType: "runsOn");
        var edge = Assert.Single(filtered.Edges);
        Assert.Equal("discovered", edge.Source);
        Assert.Contains("java", edge.Evidence ?? string.Empty);
    }

    [Fact]
    public async Task Unknown_asset_returns_helpful_error()
    {
        var service = CreateService();

        var result = await service.GetNeighborsAsync("ssh:missing");

        Assert.NotNull(result.Error);
        Assert.Contains("ssh:web-01", result.Error);
        Assert.Null(result.Root);
    }

    [Fact]
    public async Task Ambiguous_short_id_returns_candidates_instead_of_guessing()
    {
        var graph = new TopologyGraph
        {
            Nodes = new[]
            {
                new TopologyNode { Id = "ssh:web", Label = "web", Type = "ssh" },
                new TopologyNode { Id = "app:web", Label = "web-app", Type = "application" },
                new TopologyNode { Id = "app:other", Label = "other", Type = "application" }
            },
            Edges = new[]
            {
                new TopologyEdge { From = "app:web", To = "ssh:web", Type = "runsOn", Source = "manual" }
            }
        };
        var service = new AssetGraphQueryService(new FakeTopologyService(graph));

        var bySuffix = await service.GetNeighborsAsync("web");
        Assert.Null(bySuffix.Root);
        Assert.Contains("asset_ambiguous", bySuffix.Error);
        Assert.Contains("ssh:web", bySuffix.Error);
        Assert.Contains("app:web", bySuffix.Error);

        var exact = await service.GetNeighborsAsync("ssh:web");
        Assert.Null(exact.Error);
        Assert.Equal("ssh:web", exact.Root!.Id);
    }

    [Fact]
    public async Task Explain_marks_old_discovered_edges_stale_but_not_fresh_or_manual()
    {
        var graph = BuildGraph();
        var now = DateTime.UtcNow;
        graph.Edges = graph.Edges.Concat(new[]
        {
            new TopologyEdge
            {
                From = "app:batch", To = "ds:order-db", Type = "connectsTo",
                Source = "discovered", LastSeenAt = now.AddDays(-8), Evidence = "old jdbc"
            },
            new TopologyEdge
            {
                From = "app:batch", To = "app:order-service", Type = "relatedTo",
                Source = "discovered", LastSeenAt = now.AddDays(-1), Evidence = "fresh"
            },
            new TopologyEdge
            {
                From = "app:batch", To = "domain:shop.example.com", Type = "relatedTo",
                Source = "manual", LastSeenAt = now.AddDays(-30), Evidence = "manual old"
            },
            new TopologyEdge
            {
                From = "app:batch", To = "port:web-01:tcp-443", Type = "listensOn",
                Source = "discovered", LastSeenAt = now.AddDays(-30), LastVerifiedAt = now.AddDays(-1),
                Confidence = 0.77, Disputed = true, ValidFrom = now.AddDays(-2), Evidence = "recently verified"
            }
        }).ToArray();
        var service = new AssetGraphQueryService(new FakeTopologyService(graph));

        var result = await service.ExplainAsync("app:batch");

        Assert.True(result.Edges.Single(e => e.To == "ds:order-db").Stale);
        Assert.False(result.Edges.Single(e => e.To == "app:order-service").Stale);
        Assert.False(result.Edges.Single(e => e.To == "domain:shop.example.com").Stale);
        var verified = result.Edges.Single(e => e.To == "port:web-01:tcp-443");
        Assert.False(verified.Stale);
        Assert.Equal(0.77, verified.Confidence);
        Assert.True(verified.Disputed);
        Assert.NotNull(verified.ValidFrom);
        Assert.NotNull(verified.LastVerifiedAt);
    }

    [Fact]
    public async Task Search_clamps_limit_and_reports_truncation()
    {
        var graph = new TopologyGraph
        {
            Nodes = Enumerable.Range(0, 60)
                .Select(i => new TopologyNode
                {
                    Id = $"app:bulk-{i:00}", Label = $"bulk-{i:00}", Type = "application"
                })
                .ToArray(),
            Edges = Array.Empty<TopologyEdge>()
        };
        var service = new AssetGraphQueryService(new FakeTopologyService(graph));

        var limited = await service.SearchAsync("bulk", limit: 3);
        Assert.Equal(3, limited.Items.Length);
        Assert.True(limited.Truncated);

        var capped = await service.SearchAsync("bulk", limit: 999);
        Assert.Equal(50, capped.Items.Length);
        Assert.True(capped.Truncated);
    }

    [Fact]
    public async Task Neighbors_and_impact_truncate_at_node_budget()
    {
        var nodes = new List<TopologyNode>
        {
            new() { Id = "ssh:root", Label = "root", Type = "ssh" }
        };
        var edges = new List<TopologyEdge>();
        for (var i = 0; i < 250; i++)
        {
            var id = $"app:leaf-{i:000}";
            nodes.Add(new TopologyNode { Id = id, Label = id, Type = "application" });
            edges.Add(new TopologyEdge { From = id, To = "ssh:root", Type = "runsOn", Source = "manual" });
        }
        var service = new AssetGraphQueryService(new FakeTopologyService(new TopologyGraph
        {
            Nodes = nodes.ToArray(),
            Edges = edges.ToArray()
        }));

        var neighbors = await service.GetNeighborsAsync("ssh:root");
        Assert.True(neighbors.Truncated);
        Assert.True(neighbors.Nodes.Length < 250);
        Assert.Contains("已截断", neighbors.Summary);

        var impact = await service.GetImpactAsync("ssh:root");
        Assert.True(impact.Truncated);
        Assert.Equal(200, impact.Impacted.Length);
        Assert.Contains("已截断", impact.Summary);
    }

    [Fact]
    public async Task Path_caps_result_count_and_marks_truncated()
    {
        var nodes = new List<TopologyNode>
        {
            new() { Id = "app:root", Label = "root", Type = "application" },
            new() { Id = "app:leaf", Label = "leaf", Type = "application" }
        };
        var edges = new List<TopologyEdge>();
        for (var i = 0; i < 6; i++)
        {
            var mid = $"app:mid-{i}";
            nodes.Add(new TopologyNode { Id = mid, Label = mid, Type = "application" });
            edges.Add(new TopologyEdge { From = "app:root", To = mid, Type = "relatedTo", Source = "manual" });
            edges.Add(new TopologyEdge { From = mid, To = "app:leaf", Type = "relatedTo", Source = "manual" });
        }
        var service = new AssetGraphQueryService(new FakeTopologyService(new TopologyGraph
        {
            Nodes = nodes.ToArray(),
            Edges = edges.ToArray()
        }));

        var result = await service.FindPathsAsync("app:root", "app:leaf");

        Assert.Equal(5, result.Paths.Length);
        Assert.True(result.Truncated);
        Assert.Contains("截断", result.Summary);
    }

    [Fact]
    public async Task Search_handles_thousand_node_graph()
    {
        var graph = new TopologyGraph
        {
            Nodes = Enumerable.Range(0, 1000)
                .Select(i => new TopologyNode
                {
                    Id = $"app:bulk-{i:0000}", Label = $"bulk-{i:0000}", Type = "application"
                })
                .ToArray(),
            Edges = Enumerable.Range(0, 999)
                .Select(i => new TopologyEdge
                {
                    From = $"app:bulk-{i:0000}", To = $"app:bulk-{i + 1:0000}",
                    Type = "relatedTo", Source = "discovered"
                })
                .ToArray()
        };
        var service = new AssetGraphQueryService(new FakeTopologyService(graph));

        var result = await service.SearchAsync("bulk-0999", limit: 5);

        var item = Assert.Single(result.Items);
        Assert.Equal("app:bulk-0999", item.Node.Id);
        Assert.Equal(1, item.Node.Degree);
        Assert.False(result.Truncated);
    }

    private static TopologyGraph BuildGraph() => new()
    {
        Nodes = new[]
        {
            new TopologyNode
            {
                Id = "ssh:web-01", Label = "web-01", Type = "ssh",
                Info = new() { ["host"] = "10.0.0.10", ["port"] = 22, ["tags"] = new[] { "prod" } }
            },
            new TopologyNode
            {
                Id = "app:order-service", Label = "order-service", Type = "application",
                Info = new() { ["host"] = "10.0.0.10", ["port"] = 8080, ["applicationType"] = "java" }
            },
            new TopologyNode
            {
                Id = "app:batch", Label = "batch-job", Type = "application",
                Info = new() { ["host"] = "10.0.0.10", ["port"] = 8081 }
            },
            new TopologyNode
            {
                Id = "ds:order-db", Label = "订单库", Type = "datasource",
                Info = new() { ["host"] = "10.0.0.20", ["port"] = 3306, ["datasourceType"] = "mysql" }
            },
            new TopologyNode
            {
                Id = "domain:shop.example.com", Label = "shop.example.com", Type = "domain",
                Info = new() { ["host"] = "10.0.0.10", ["port"] = 443 }
            },
            new TopologyNode
            {
                Id = "cert:abc123", Label = "shop-cert", Type = "cert",
                Info = new() { ["expiresAt"] = "2027-01-01" }
            },
            new TopologyNode
            {
                Id = "port:web-01:tcp-443", Label = "tcp/443 (nginx)", Type = "port",
                Info = new() { ["port"] = 443, ["protocol"] = "tcp", ["process"] = "nginx" }
            },
            new TopologyNode
            {
                Id = "app:disc:foo", Label = "unknown-app (待确认)", Type = "application",
                Info = new() { ["ports"] = "9090" }
            }
        },
        Edges = new[]
        {
            new TopologyEdge { From = "app:order-service", To = "ssh:web-01", Type = "runsOn", Source = "discovered", Evidence = "java process" },
            new TopologyEdge { From = "app:batch", To = "ssh:web-01", Type = "runsOn", Source = "manual" },
            new TopologyEdge { From = "app:order-service", To = "ds:order-db", Type = "connectsTo", Source = "discovered", Evidence = "jdbc:mysql://10.0.0.20:3306" },
            new TopologyEdge { From = "domain:shop.example.com", To = "app:order-service", Type = "exposes", Source = "discovered", Evidence = "nginx server_name" },
            new TopologyEdge { From = "domain:shop.example.com", To = "cert:abc123", Type = "tlsBy", Source = "discovered" },
            new TopologyEdge { From = "app:disc:foo", To = "ssh:web-01", Type = "runsOn", Source = "discovered" }
        }
    };

    private sealed class FakeTopologyService(TopologyGraph graph) : ITopologyService
    {
        public Task<TopologyGraph> GetGraphAsync(CancellationToken ct = default) => Task.FromResult(graph);

        public Task<DependencyGraph> GetDependenciesAsync(string assetId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DiscoveryResult> DiscoverAsync(string[]? serverIds, string[]? searchPaths, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
