using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

public class AssetConfigurationConsistencyServiceTests
{
    [Fact]
    public async Task Reports_unobserved_datasource_and_url()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web", Host = "10.0.0.1", AssetKey = "web" } },
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "app1", Name = "order", Port = 8080, AssetKey = "order",
                    RuntimeKind = RuntimeEnvironmentKind.SshServer, RuntimeSshAssetKey = "web",
                    Urls = new[]
                    {
                        new UrlPattern { Url = "https://shop.example.com/", Role = UrlRole.Backend },
                        new UrlPattern { Url = "https://other.example.com/", Role = UrlRole.Backend }
                    }
                }
            },
            DataSources = new[]
            {
                new DataSourceConfig
                {
                    Id = "ds1", Name = "orderdb", Type = "mysql", Host = "10.0.0.1", Port = 3306,
                    RuntimeKind = RuntimeEnvironmentKind.SshServer, RuntimeSshAssetKey = "web"
                }
            }
        };

        var evidence = new ServerDiscoveryEvidence { ServerId = "s1", CapturedAt = "2026-01-01" };
        evidence.Ports.Add(new DiscoveryPortEvidence { Port = 8080, Process = "java" });
        evidence.Ports.Add(new DiscoveryPortEvidence { Port = 3307, Process = "mysqld" });
        evidence.Domains.Add(new DiscoveryDomainEvidence { Domain = "shop.example.com", Ports = new[] { 443 } });

        var service = new AssetConfigurationConsistencyService(
            new MemoryConfigService(config), new FakeTopology(evidence));

        var issues = await service.GetIssuesAsync();

        Assert.Contains(issues, i => i.Code == "datasource_not_observed" && i.AssetId == "ds1");
        Assert.Contains(issues, i => i.Code == "url_not_observed" && i.Message.Contains("other.example.com"));
        Assert.DoesNotContain(issues, i => i.Code == "application_not_observed" && i.AssetId == "app1");
    }

    [Fact]
    public async Task Reports_missing_evidence()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web", Host = "10.0.0.1", AssetKey = "web" } },
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "app1", Name = "order", AssetKey = "order",
                    RuntimeKind = RuntimeEnvironmentKind.SshServer, RuntimeSshAssetKey = "web"
                }
            }
        };

        var service = new AssetConfigurationConsistencyService(
            new MemoryConfigService(config), new FakeTopology(null));

        var issues = await service.GetIssuesAsync();

        Assert.Contains(issues, i => i.Code == "no_discovery_evidence" && i.AssetId == "app1");
    }

    private sealed class MemoryConfigService : IConfigService
    {
        private readonly AppConfig _config;
        public MemoryConfigService(AppConfig config) => _config = config;
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }

    private sealed class FakeTopology : ITopologyService
    {
        private readonly ServerDiscoveryEvidence? _evidence;
        public FakeTopology(ServerDiscoveryEvidence? evidence) => _evidence = evidence;
        public Task<TopologyGraph> GetGraphAsync(CancellationToken ct = default) => Task.FromResult(new TopologyGraph());
        public Task<TopologyGraph> GetCuratedGraphAsync(CancellationToken ct = default) => Task.FromResult(new TopologyGraph());
        public Task<DependencyGraph> GetDependenciesAsync(string assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DiscoveryResult> DiscoverAsync(string[]? serverIds, string[]? searchPaths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServerDiscoveryEvidence?> GetServerDiscoveryEvidenceAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(_evidence);
    }
}
