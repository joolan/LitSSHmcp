using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

public class AssetConfigurationSuggestionServiceTests
{
    [Fact]
    public async Task Suggests_asset_key_runtime_and_nginx_url()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web-01", Host = "10.0.0.1", AssetKey = "web" }
            },
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "app1", Name = "order", Type = "java", ContainerName = "order-svc"
                },
                new ApplicationConfig
                {
                    Id = "nginx", Name = "nginx", Type = "nginx", AssetKey = "nginx",
                    RuntimeKind = RuntimeEnvironmentKind.SshServer, RuntimeSshAssetKey = "web"
                }
            },
            DataSources = new[]
            {
                new DataSourceConfig
                {
                    Id = "ds1", Name = "orderdb", Type = "mysql", Host = "10.0.0.1", Port = 3306
                }
            }
        };

        var evidence = new ServerDiscoveryEvidence { ServerId = "s1", CapturedAt = "2026-01-01" };
        evidence.Containers.Add(new DiscoveryContainerEvidence { Name = "order-svc", Image = "order:1" });
        evidence.Domains.Add(new DiscoveryDomainEvidence { Domain = "shop.example.com", Ssl = true, Ports = new[] { 443 } });
        var service = new AssetConfigurationSuggestionService(
            new MemoryConfigService(config), new FakeTopology(evidence));

        var suggestions = await service.GetSuggestionsAsync();

        Assert.Contains(suggestions, s => s.Kind == AssetSuggestionKind.AssetKey && s.AssetId == "app1");
        Assert.Contains(suggestions, s => s.Kind == AssetSuggestionKind.RuntimeSsh && s.AssetId == "app1" && s.Value == "web");
        Assert.Contains(suggestions, s => s.Kind == AssetSuggestionKind.RuntimeSsh && s.AssetId == "ds1" && s.Value == "web");
        Assert.Contains(suggestions, s => s.Kind == AssetSuggestionKind.Url && s.AssetId == "nginx" && s.Value.Contains("shop.example.com"));
    }

    [Fact]
    public async Task Apply_updates_config_and_persists()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web", Host = "10.0.0.1", AssetKey = "web" } },
            Applications = new[] { new ApplicationConfig { Id = "app1", Name = "order" } }
        };
        var configService = new MemoryConfigService(config);
        var service = new AssetConfigurationSuggestionService(configService, new FakeTopology(null));

        var suggestion = new AssetConfigurationSuggestion
        {
            AssetId = "app1", AssetType = "application", AssetName = "order",
            Kind = AssetSuggestionKind.RuntimeSsh, Value = "web"
        };

        Assert.True(await service.ApplyAsync(suggestion));
        var app = config.Applications[0];
        Assert.Equal(RuntimeEnvironmentKind.SshServer, app.RuntimeKind);
        Assert.Equal("web", app.RuntimeSshAssetKey);
        Assert.True(configService.Saved);
    }

    private sealed class MemoryConfigService : IConfigService
    {
        private readonly AppConfig _config;
        public MemoryConfigService(AppConfig config) => _config = config;
        public bool Saved { get; private set; }
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) { Saved = true; return Task.CompletedTask; }
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
