using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TopologyTimelineViewModelTests
{
    [Fact]
    public async Task Load_maps_points_and_selects_latest()
    {
        var query = new FakeQuery
        {
            Timeline = new GraphTimelineResult
            {
                ServerId = "s1",
                Summary = "2 个时间点",
                Points = new[]
                {
                    new GraphTimelinePoint { SnapshotId = 1, CapturedAt = "2026-10-18T10:00:00Z", Success = true },
                    new GraphTimelinePoint
                    {
                        SnapshotId = 2, CapturedAt = "2026-10-19T10:00:00Z", Success = true,
                        Nodes = new[] { new GraphNodeRef { Id = "service:s1:nginx", Type = "service" } },
                        Edges = new[] { new GraphEdgeRef { From = "app:a1", To = "ds:d1", Type = "connectsTo" } },
                        AddedNodes = new[] { "service:s1:nginx" },
                        RemovedNodes = new[] { "port:s1:tcp-80" },
                        AddedEdges = new[] { "app:a1|connectsTo|ds:d1" }
                    }
                }
            }
        };
        var vm = new TopologyTimelineViewModel(query, new FakeConfig());

        await vm.InitializeAsync("s1");
        vm.SelectedLimit = 5;
        await vm.LoadAsync();

        Assert.Equal(2, vm.Points.Count);
        Assert.Equal(2, vm.SelectedPoint!.Point.SnapshotId);
        Assert.Equal("2 个时间点", vm.Summary);
        Assert.Equal(3, vm.Changes.Count);
        Assert.Contains(vm.Changes, c => c.Added && c.Text == "service:s1:nginx");
        Assert.Contains(vm.Changes, c => !c.Added && c.Text == "port:s1:tcp-80");
        Assert.Contains(vm.Changes, c => c.Added && c.Text == "app:a1 -connectsTo-> ds:d1");
        Assert.Equal(5, query.RequestedLimit);
    }

    [Fact]
    public async Task Load_reports_query_error()
    {
        var query = new FakeQuery
        {
            Timeline = new GraphTimelineResult { Error = "还没有快照", Summary = "还没有快照" }
        };
        var vm = new TopologyTimelineViewModel(query, new FakeConfig());
        await vm.InitializeAsync("s1");

        await vm.LoadAsync();

        Assert.Empty(vm.Points);
        Assert.Equal("还没有快照", vm.StatusMessage);
    }

    [Fact]
    public void BuildChanges_keeps_snapshot_order_and_labels()
    {
        var point = new GraphTimelinePoint
        {
            AddedEdges = new[] { "domain:shop.example.com|tlsBy|cert:abc" },
            RemovedEdges = new[] { "app:a1|runsOn|ssh:s1" }
        };

        var rows = TopologyTimelineViewModel.BuildChanges(point);

        Assert.Collection(rows,
            r => Assert.Equal(("关系 +", "domain:shop.example.com -tlsBy-> cert:abc", true), (r.Kind, r.Text, r.Added)),
            r => Assert.Equal(("关系 -", "app:a1 -runsOn-> ssh:s1", false), (r.Kind, r.Text, r.Added)));
    }

    private sealed class FakeConfig : IConfigService
    {
        private readonly AppConfig _config = new()
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" },
                new SshServerConfig { Id = "s2", Name = "web2", Host = "10.0.0.2" }
            }
        };

        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => "config.json";
    }

    private sealed class FakeQuery : IAssetGraphQueryService
    {
        public GraphTimelineResult Timeline { get; set; } = new();
        public int RequestedLimit { get; private set; }

        public Task<GraphTimelineResult> GetTimelineAsync(string serverId, int limit = 10, CancellationToken ct = default)
        {
            RequestedLimit = limit;
            return Task.FromResult(Timeline);
        }

        public Task<GraphSearchResult> SearchAsync(string query, string[]? kinds = null, int limit = 20, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GraphNeighborhood> GetNeighborsAsync(string assetId, int depth = 1, string[]? relationTypes = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GraphPathResult> FindPathsAsync(string fromId, string toId, int maxHops = 6, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GraphImpactResult> GetImpactAsync(string assetId, int maxDepth = 6, bool includeUnconfirmed = false, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GraphExplainResult> ExplainAsync(string assetId, string? toId = null, string? relationType = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GraphDiffResult> GetDiffAsync(string serverId, long? fromSnapshotId = null, long? toSnapshotId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
