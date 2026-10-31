using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AssetTopologyExplorerViewModelTests
{
    [Fact]
    public async Task Load_builds_server_lanes_and_cross_server_dependencies()
    {
        var vm = new AssetTopologyExplorerViewModel(new FakeTopologyService(Graph()));

        await vm.LoadAsync();

        Assert.Equal(2, vm.Lanes.Count);
        var web = Assert.Single(vm.Lanes, l => l.Server.Id == "ssh:s1");
        Assert.Contains(web.Children, a => a.Id == "app:order");
        var db = Assert.Single(vm.Lanes, l => l.Server.Id == "ssh:s2");
        Assert.Contains(db.Children, a => a.Id == "ds:mysql");
        Assert.Single(vm.EntryAssets, a => a.Id == "domain:shop.example.com");
        Assert.Equal(2, vm.Dependencies.Count);
        Assert.Contains(vm.Dependencies, d => d.Relation == "connectsTo" && d.IsCrossServer);
        Assert.Contains(vm.Dependencies, d => d.Relation == "accessedVia" && d.IsCrossServer);
    }

    [Fact]
    public async Task Search_filters_lanes_and_show_all_dependencies_expands_same_host_edges()
    {
        var vm = new AssetTopologyExplorerViewModel(new FakeTopologyService(Graph()));
        await vm.LoadAsync();

        vm.SearchText = "order";
        Assert.Single(vm.Lanes);
        Assert.Contains(vm.Lanes[0].Children, a => a.Id == "app:order");

        vm.SearchText = string.Empty;
        vm.ShowAllDependencies = true;
        Assert.Contains(vm.Dependencies, d => d.Relation == "exposes" && !d.IsCrossServer);
    }

    [Fact]
    public async Task Flow_view_builds_upstream_downstream_groups()
    {
        var vm = new AssetTopologyExplorerViewModel(new FakeTopologyService(Graph()));
        await vm.LoadAsync();

        vm.SelectedFlowTarget = vm.FlowTargets.Single(a => a.Id == "app:order");

        Assert.Contains(vm.FlowGroups, g => g.Title == "上游 / 入口");
        Assert.Contains(vm.FlowGroups, g => g.Title == "当前资产");
        Assert.Contains(vm.FlowGroups, g => g.Title == "下游 / 依赖");
        Assert.Contains(vm.FlowGroups, g => g.Title == "运行宿主");
        Assert.Contains("宿主", vm.FlowSummary);
        Assert.Contains(vm.FlowPaths, p => p.Text.Contains("connectsTo", StringComparison.Ordinal));
        Assert.Contains(vm.FlowGroups.SelectMany(g => g.Nodes),
            a => a.Id == "ds:mysql" && a.IsHighlighted);
    }

    [Fact]
    public async Task Matrix_view_counts_cross_server_relations()
    {
        var vm = new AssetTopologyExplorerViewModel(new FakeTopologyService(Graph()));
        await vm.LoadAsync();

        Assert.Equal(new[] { "db1", "web1" }, vm.MatrixColumns);
        var webRow = vm.MatrixRows.Single(r => r.ServerLabel == "web1");
        Assert.Equal("2", webRow.Cells[0].Text);

        vm.ShowAllDependencies = true;
        webRow = vm.MatrixRows.Single(r => r.ServerLabel == "web1");
        Assert.True(webRow.Cells[1].HasValue);
    }

    [Fact]
    public async Task Select_populates_selected_relations()
    {
        var vm = new AssetTopologyExplorerViewModel(new FakeTopologyService(Graph()));
        await vm.LoadAsync();

        var app = vm.Lanes.Single(l => l.Server.Id == "ssh:s1").Children.Single(a => a.Id == "app:order");
        vm.SelectCommand.Execute(app);

        Assert.Equal("order-service", vm.SelectedTitle);
        Assert.Contains(vm.SelectedRelations, r => r.Contains("runsOn"));
        Assert.Contains(vm.SelectedRelations, r => r.Contains("connectsTo"));
    }

    private static TopologyGraph Graph() => new()
    {
        Nodes = new[]
        {
            new TopologyNode
            {
                Id = "ssh:s1", Label = "web1", Type = "ssh",
                Info = new Dictionary<string, object?>
                {
                    ["host"] = "10.0.0.1", ["port"] = 22, ["accessProfileCount"] = 2
                }
            },
            new TopologyNode
            {
                Id = "ssh:s2", Label = "db1", Type = "ssh",
                Info = new Dictionary<string, object?> { ["host"] = "10.0.0.2", ["port"] = 22 }
            },
            new TopologyNode { Id = "app:order", Label = "order-service", Type = "application" },
            new TopologyNode { Id = "ds:mysql", Label = "mysql-01", Type = "datasource" },
            new TopologyNode { Id = "domain:shop.example.com", Label = "shop.example.com", Type = "domain" }
        },
        Edges = new[]
        {
            new TopologyEdge { From = "app:order", To = "ssh:s1", Type = "runsOn", Source = "discovered" },
            new TopologyEdge { From = "ds:mysql", To = "ssh:s2", Type = "runsOn", Source = "discovered" },
            new TopologyEdge { From = "domain:shop.example.com", To = "app:order", Type = "exposes", Source = "discovered" },
            new TopologyEdge { From = "domain:shop.example.com", To = "ssh:s1", Type = "resolvesTo", Source = "discovered" },
            new TopologyEdge { From = "app:order", To = "ds:mysql", Type = "connectsTo", Source = "discovered" },
            new TopologyEdge { From = "ssh:s1", To = "ssh:s2", Type = "accessedVia", Source = "manual" }
        }
    };

    private sealed class FakeTopologyService : ITopologyService
    {
        private readonly TopologyGraph _graph;
        public FakeTopologyService(TopologyGraph graph) => _graph = graph;
        public Task<TopologyGraph> GetGraphAsync(CancellationToken ct = default) => Task.FromResult(_graph);
        public Task<DependencyGraph> GetDependenciesAsync(string assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DiscoveryResult> DiscoverAsync(string[]? serverIds, string[]? searchPaths, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
