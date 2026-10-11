using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using LitSSHmcp.McpServer.Resources;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>验证 MCP Resources：只读、可复用、**不泄露密码**、禁用服务器排除、指南可用。</summary>
public class ResourceTests
{
    [Fact]
    public async Task Servers_resource_excludes_disabled_and_secrets()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web", Host = "10.0.0.1", Port = 22, Username = "root", Password = "SECRET-PW" },
                new SshServerConfig { Id = "s2", Name = "off", Host = "10.0.0.2", Password = "SECRET2", Disabled = true }
            }
        };

        var json = await new SshResources(new StubConfig(config)).GetServers();

        Assert.Contains("10.0.0.1", json);
        Assert.Contains("\"s1\"", json);
        Assert.DoesNotContain("SECRET-PW", json);
        Assert.DoesNotContain("SECRET2", json);
        Assert.DoesNotContain("10.0.0.2", json);   // 禁用服务器被排除
    }

    [Fact]
    public async Task Datasources_resource_has_no_password()
    {
        var config = new AppConfig
        {
            DataSources = new[]
            {
                new DataSourceConfig { Id = "d1", Name = "db", Type = "mysql", Host = "10.0.0.1", Port = 3306, Username = "u", Password = "SECRET-DB" }
            }
        };

        var json = await new DatasourceResources(new StubConfig(config)).GetDatasources();

        Assert.Contains("\"d1\"", json);
        Assert.DoesNotContain("SECRET-DB", json);
    }

    [Fact]
    public async Task Guide_resource_returns_group_aware_guide()
    {
        var text = await new GuideResources(new StubConfig(new AppConfig())).GetGuide();

        Assert.Contains("LitSSH MCP 使用指南", text);
        Assert.Contains("tools", text);
    }

    [Fact]
    public async Task Snapshot_resource_returns_resource_overview_without_secret()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web", Host = "10.0.0.1", Username = "root", Password = "SECRET-PW" }
            }
        };
        const string dataJson =
            "{\"sections\":{\"resource\":{\"status\":\"ok\",\"durationMs\":12,\"data\":{\"load\":{\"1m\":0.5}," +
            "\"memory\":{\"usedPercent\":72.0,\"swapUsedPercent\":10.0},\"disks\":[{\"mount\":\"/\",\"usedPercent\":81}]," +
            "\"cpu\":{\"cores\":4},\"warnings\":[{\"severity\":\"warning\",\"metric\":\"disk\"}],\"riskLevel\":\"warning\"}}," +
            "\"docker\":{\"status\":\"skipped\"}}}";
        var store = new FakeSnapshotStore(new ServerSnapshot
        {
            Id = 1, ServerId = "s1", ServerName = "web", CreatedAt = "2026-01-01T00:00:00.0000000Z",
            Status = SnapshotStatus.Succeeded, DataJson = dataJson
        });

        var json = await new SnapshotResources(store, new StubConfig(config)).GetLatestSnapshot("s1");

        Assert.Contains("\"found\": true", json);
        Assert.Contains("riskLevel", json);
        Assert.Contains("usedPercent", json);
        Assert.DoesNotContain("SECRET-PW", json);
    }

    [Fact]
    public async Task Snapshot_resource_reports_not_found_for_unknown_server()
    {
        var json = await new SnapshotResources(new FakeSnapshotStore(null), new StubConfig(new AppConfig()))
            .GetLatestSnapshot("nope");

        Assert.Contains("\"found\": false", json);
        Assert.Contains("server_not_found", json);
    }

    [Fact]
    public async Task Topology_resource_returns_graph()
    {
        var graph = new TopologyGraph
        {
            Nodes = new[] { new TopologyNode { Id = "ssh:web", Label = "web", Type = "ssh" } },
            Edges = Array.Empty<TopologyEdge>()
        };

        var json = await new TopologyResources(new FakeTopologyService(graph)).GetTopology();

        Assert.Contains("ssh:web", json);
    }

    private sealed class FakeSnapshotStore : ISnapshotStore
    {
        private readonly ServerSnapshot? _latest;
        public FakeSnapshotStore(ServerSnapshot? latest) => _latest = latest;

        public Task<ServerSnapshot?> GetLatestAsync(string serverId, CancellationToken ct = default) => Task.FromResult(_latest);

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<long?> BeginAsync(string serverId, string serverName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AppendEventAsync(long snapshotId, string serverId, string kind, string? collector = null, string? message = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CompleteAsync(long snapshotId, string dataJson, double durationMs, string? escalation, int collectorVersion, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FailAsync(long snapshotId, string? error, double durationMs, string? dataJson = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ServerSnapshot?> GetByIdAsync(long id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ServerSnapshot?> GetRunningAsync(string serverId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<ServerSnapshot>> GetRecentAsync(string serverId, int limit = 10, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<SnapshotEvent>> GetEventsAsync(long snapshotId, int limit = 50, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneAsync(string serverId, int keepPerServer, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeTopologyService : ITopologyService
    {
        private readonly TopologyGraph _graph;
        public FakeTopologyService(TopologyGraph graph) => _graph = graph;

        public Task<TopologyGraph> GetGraphAsync(CancellationToken ct = default) => Task.FromResult(_graph);
        public Task<DependencyGraph> GetDependenciesAsync(string assetId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DiscoveryResult> DiscoverAsync(string[]? serverIds, string[]? searchPaths, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubConfig : IConfigService
    {
        private readonly AppConfig _config;
        public StubConfig(AppConfig config) => _config = config;
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }
}
