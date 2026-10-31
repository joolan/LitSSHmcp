using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

public class UrlResolutionServiceTests
{
    [Fact]
    public void Resolves_nginx_entry_and_load_balanced_backends()
    {
        var config = Config();
        var result = UrlResolutionService.Resolve(config, "https://shop.example.com/api/order/1");

        Assert.True(result.Success, result.Error);
        var entry = Assert.Single(result.EntryApps);
        Assert.Equal("nginx-shop", entry.Name);
        Assert.Equal(2, result.BackendApps.Count);
        Assert.Equal(2, result.LandingApps.Count);
        Assert.Equal(2, result.RuntimeChains.Count);
        Assert.Contains(result.RuntimeChains, c =>
            c.ApplicationName == "order-service" &&
            c.DockerApplicationAssetKey == "docker-order" &&
            c.ServerAssetKey == "web");
        Assert.Contains(result.RuntimeChains, c =>
            c.ApplicationName == "order-service-2" &&
            c.ServerAssetKey == "db");
    }

    [Fact]
    public void Longest_prefix_wins_and_path_boundary_is_enforced()
    {
        var config = Config();

        var exact = UrlResolutionService.Resolve(config, "https://shop.example.com/api/order/1");
        Assert.Contains(exact.LandingApps, a => a.Name == "order-service");

        var boundary = UrlResolutionService.Resolve(config, "https://shop.example.com/apix");
        Assert.False(boundary.Success);
        Assert.Equal("url_not_found", boundary.Error);
    }

    [Fact]
    public void Auto_role_becomes_landing_when_no_backend_exists()
    {
        var config = new AppConfig
        {
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "app1", Name = "standalone-app", Type = "java", AssetKey = "standalone",
                    RuntimeKind = RuntimeEnvironmentKind.Standalone,
                    Urls = new[] { new UrlPattern { Url = "https://example.com/", Role = UrlRole.Auto } }
                }
            }
        };

        var result = UrlResolutionService.Resolve(config, "https://example.com/path");

        Assert.True(result.Success);
        Assert.Single(result.LandingApps);
        Assert.Equal("standalone-app", result.LandingApps[0].Name);
        Assert.True(Assert.Single(result.RuntimeChains).Complete);
    }

    [Fact]
    public void Wildcard_host_matches_subdomain()
    {
        var config = new AppConfig
        {
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "app1", Name = "wildcard-app", Type = "java", AssetKey = "wildcard",
                    RuntimeKind = RuntimeEnvironmentKind.Standalone,
                    Urls = new[] { new UrlPattern { Url = "*.example.com/api", Role = UrlRole.Auto } }
                }
            }
        };

        var result = UrlResolutionService.Resolve(config, "https://api.example.com/api/orders");

        Assert.True(result.Success, result.Error);
        Assert.Equal("wildcard-app", Assert.Single(result.LandingApps).Name);
    }

    [Fact]
    public void Missing_runtime_target_is_reported_in_chain()
    {
        var config = new AppConfig
        {
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "app1", Name = "orphan", Type = "java", AssetKey = "orphan",
                    RuntimeKind = RuntimeEnvironmentKind.SshServer,
                    RuntimeSshAssetKey = "missing",
                    Urls = new[] { new UrlPattern { Url = "https://orphan.example.com", Role = UrlRole.Auto } }
                }
            }
        };

        var result = UrlResolutionService.Resolve(config, "https://orphan.example.com/");

        Assert.True(result.Success);
        var chain = Assert.Single(result.RuntimeChains);
        Assert.False(chain.Complete);
        Assert.NotNull(chain.Error);
    }

    private static AppConfig Config() => new()
    {
        Servers = new[]
        {
            new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1", AssetKey = "web" },
            new SshServerConfig { Id = "s2", Name = "db1", Host = "10.0.0.2", AssetKey = "db" }
        },
        Applications = new[]
        {
            new ApplicationConfig
            {
                Id = "nginx", Name = "nginx-shop", Type = "nginx", AssetKey = "nginx",
                RuntimeKind = RuntimeEnvironmentKind.SshServer, RuntimeSshAssetKey = "web",
                Urls = new[] { new UrlPattern { Url = "https://shop.example.com/api/", Role = UrlRole.PublicEntry } }
            },
            new ApplicationConfig
            {
                Id = "docker", Name = "docker-order", Type = "docker", AssetKey = "docker-order",
                RuntimeKind = RuntimeEnvironmentKind.SshServer, RuntimeSshAssetKey = "web"
            },
            new ApplicationConfig
            {
                Id = "app1", Name = "order-service", Type = "java", AssetKey = "order-1",
                RuntimeKind = RuntimeEnvironmentKind.DockerApplication,
                RuntimeDockerAssetKey = "docker-order", RuntimeContainerName = "order-api",
                Urls = new[] { new UrlPattern { Url = "https://shop.example.com/api/order", Role = UrlRole.Backend } }
            },
            new ApplicationConfig
            {
                Id = "app2", Name = "order-service-2", Type = "java", AssetKey = "order-2",
                RuntimeKind = RuntimeEnvironmentKind.SshServer, RuntimeSshAssetKey = "db",
                Urls = new[] { new UrlPattern { Url = "https://shop.example.com/api/order", Role = UrlRole.Backend } }
            }
        }
    };
}
