using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

public class GraphTimelineTests
{
    [Fact]
    public async Task Timeline_replays_snapshots_and_reports_added_units()
    {
        var store = new FakeSnapshotStore();
        store.Items.Add(Snapshot(2, "2026-10-19T10:00:00.0000000Z", "nginx.service", "redis.service"));
        store.Items.Add(Snapshot(1, "2026-10-18T10:00:00.0000000Z", "nginx.service"));
        var service = new AssetGraphQueryService(new FakeTopologyService(), store, new FakeConfigService());

        var result = await service.GetTimelineAsync("s1");

        Assert.Null(result.Error);
        Assert.Equal(2, result.Points.Length);
        Assert.Equal(1, result.Points[0].SnapshotId);
        Assert.Empty(result.Points[0].AddedNodes);
        Assert.Contains("service:s1:redis", result.Points[1].AddedNodes);
        Assert.Contains(result.Points[1].Edges, e =>
            e.From == "service:s1:redis" && e.To == "ssh:s1" && e.Type == "runsOn");
        Assert.Contains("nginx.service", result.Points[1].Nodes.Single(n => n.Id == "service:s1:nginx").Label);
    }

    [Fact]
    public async Task Diff_defaults_to_latest_two_snapshots()
    {
        var store = new FakeSnapshotStore();
        store.Items.Add(Snapshot(2, "2026-10-19T10:00:00.0000000Z", "nginx.service", "redis.service"));
        store.Items.Add(Snapshot(1, "2026-10-18T10:00:00.0000000Z", "nginx.service"));
        var service = new AssetGraphQueryService(new FakeTopologyService(), store, new FakeConfigService());

        var result = await service.GetDiffAsync("s1");

        Assert.Null(result.Error);
        Assert.Contains(result.AddedNodes, n => n.Id == "service:s1:redis");
        Assert.Empty(result.RemovedNodes);
        Assert.Contains(result.AddedEdges, e => e.From == "service:s1:redis" && e.Type == "runsOn");
        Assert.Contains("快照 1 → 2", result.Summary);
    }

    [Fact]
    public async Task Timeline_reports_access_route_changes()
    {
        var store = new FakeSnapshotStore();
        store.Items.Add(AccessSnapshot(2, "2026-10-19T10:00:00.0000000Z", "jumpB", new[] { "ds1" }));
        store.Items.Add(AccessSnapshot(1, "2026-10-18T10:00:00.0000000Z", "jumpA", Array.Empty<string>()));
        var service = new AssetGraphQueryService(new FakeTopologyService(), store, new FakeConfigService());

        var result = await service.GetTimelineAsync("s1");

        Assert.Null(result.Error);
        Assert.Contains(result.Points[1].AddedEdges, k =>
            k == "ssh:s1|ssh:jumpB|accessedVia");
        Assert.Contains(result.Points[1].RemovedEdges, k =>
            k == "ssh:s1|ssh:jumpA|accessedVia");
        Assert.Contains(result.Points[1].AddedEdges, k =>
            k == "ds:ds1|ssh:s1|accessedVia");
    }

    [Fact]
    public async Task Timeline_requires_snapshot_dependencies()
    {
        var service = new AssetGraphQueryService(new FakeTopologyService());

        var result = await service.GetTimelineAsync("s1");

        Assert.NotNull(result.Error);
        Assert.Empty(result.Points);
    }

    private static ServerSnapshot Snapshot(long id, string createdAt, params string[] units) => new()
    {
        Id = id,
        ServerId = "s1",
        ServerName = "web1",
        CreatedAt = createdAt,
        CompletedAt = createdAt,
        Status = SnapshotStatus.Succeeded,
        DataJson = JsonSerializer.Serialize(new
        {
            sections = new
            {
                systemd = new
                {
                    status = "ok",
                    data = new
                    {
                        units = units.Select(unit => new
                        {
                            unit,
                            load = "loaded",
                            active = "active",
                            sub = "running"
                        }).ToArray()
                    }
                }
            }
        })
    };

    private static ServerSnapshot AccessSnapshot(
        long id, string createdAt, string jumpId, string[] tunnels) => new()
    {
        Id = id,
        ServerId = "s1",
        ServerName = "web1",
        CreatedAt = createdAt,
        CompletedAt = createdAt,
        Status = SnapshotStatus.Succeeded,
        DataJson = JsonSerializer.Serialize(new
        {
            sections = new
            {
                topology_access = new
                {
                    status = "ok",
                    data = new
                    {
                        sshJump = new { targetId = "s1", jumpId, jumpName = jumpId },
                        tunnels = tunnels.Select(ds => new { datasourceId = ds, datasourceName = ds }).ToArray()
                    }
                }
            }
        })
    };

    private sealed class FakeSnapshotStore : ISnapshotStore
    {
        public List<ServerSnapshot> Items { get; } = new();

        public Task<List<ServerSnapshot>> GetRecentAsync(string serverId, int limit = 10, CancellationToken ct = default) =>
            Task.FromResult(Items.Where(s => s.ServerId == serverId).OrderByDescending(s => s.CreatedAt).Take(limit).ToList());

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<long?> BeginAsync(string serverId, string serverName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AppendEventAsync(long snapshotId, string serverId, string kind, string? collector = null, string? message = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteAsync(long snapshotId, string dataJson, double durationMs, string? escalation, int collectorVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task FailAsync(long snapshotId, string? error, double durationMs, string? dataJson = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServerSnapshot?> GetLatestAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServerSnapshot?> GetByIdAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServerSnapshot?> GetRunningAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<SnapshotEvent>> GetEventsAsync(long snapshotId, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PruneAsync(string serverId, int keepPerServer, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeConfigService : IConfigService
    {
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } }
        });
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => "config.json";
    }

    private sealed class FakeTopologyService : ITopologyService
    {
        public Task<TopologyGraph> GetGraphAsync(CancellationToken ct = default) =>
            Task.FromResult(new TopologyGraph());
        public Task<DependencyGraph> GetDependenciesAsync(string assetId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DiscoveryResult> DiscoverAsync(string[]? serverIds, string[]? searchPaths, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
