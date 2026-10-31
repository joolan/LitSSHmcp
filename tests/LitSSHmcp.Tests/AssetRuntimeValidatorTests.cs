using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

public class AssetRuntimeValidatorTests
{
    [Fact]
    public void Valid_runtime_configuration_has_no_errors()
    {
        var config = new AppConfig
        {
            Servers = new[] { Server("web", "web-01") },
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "docker1", Name = "docker-app", Type = "docker", AssetKey = "docker-01",
                    RuntimeKind = RuntimeEnvironmentKind.Standalone
                },
                new ApplicationConfig
                {
                    Id = "app1", Name = "order", Type = "java", AssetKey = "order-service",
                    RuntimeKind = RuntimeEnvironmentKind.DockerApplication,
                    RuntimeDockerAssetKey = "docker-01",
                    RuntimeContainerName = "order-api",
                    Urls = new[] { new UrlPattern { Url = "https://shop.example.com/api/order", Role = UrlRole.Backend } }
                }
            },
            DataSources = new[]
            {
                new DataSourceConfig
                {
                    Id = "ds1", Name = "orderdb", Type = "mysql", AssetKey = "order-db",
                    RuntimeKind = RuntimeEnvironmentKind.SshServer,
                    RuntimeSshAssetKey = "web"
                }
            }
        };

        var result = AssetRuntimeValidator.Validate(config);

        Assert.False(result.HasErrors);
        Assert.DoesNotContain(result.Issues, i => i.Code is "runtime_target_missing" or "runtime_target_not_found" or "runtime_cycle");
    }

    [Fact]
    public void Missing_runtime_target_and_container_are_reported()
    {
        var config = new AppConfig
        {
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "app1", Name = "order", Type = "java", AssetKey = "order-service",
                    RuntimeKind = RuntimeEnvironmentKind.DockerApplication
                }
            }
        };

        var result = AssetRuntimeValidator.Validate(config);

        Assert.Contains(result.Issues, i => i.Code == "runtime_target_missing");
    }

    [Fact]
    public void Docker_target_must_be_docker_type()
    {
        var config = new AppConfig
        {
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "java1", Name = "java-app", Type = "java", AssetKey = "java-app",
                    RuntimeKind = RuntimeEnvironmentKind.Standalone
                },
                new ApplicationConfig
                {
                    Id = "app1", Name = "order", Type = "java", AssetKey = "order-service",
                    RuntimeKind = RuntimeEnvironmentKind.DockerApplication,
                    RuntimeDockerAssetKey = "java-app",
                    RuntimeContainerName = "order-api"
                }
            }
        };

        var result = AssetRuntimeValidator.Validate(config);

        Assert.Contains(result.Issues, i => i.Code == "runtime_target_type_mismatch");
    }

    [Fact]
    public void Docker_application_cannot_run_in_docker_environment()
    {
        var config = new AppConfig
        {
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "docker1", Name = "docker-app", Type = "docker", AssetKey = "docker-01",
                    RuntimeKind = RuntimeEnvironmentKind.Standalone
                },
                new ApplicationConfig
                {
                    Id = "docker2", Name = "nested", Type = "docker", AssetKey = "docker-02",
                    RuntimeKind = RuntimeEnvironmentKind.DockerApplication,
                    RuntimeDockerAssetKey = "docker-01",
                    RuntimeContainerName = "nested"
                }
            }
        };

        var result = AssetRuntimeValidator.Validate(config);

        Assert.Contains(result.Issues, i => i.Code == "docker_nested_not_allowed");
    }

    [Fact]
    public void Docker_runtime_requires_container_or_compose_service()
    {
        var config = new AppConfig
        {
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "docker1", Name = "docker-app", Type = "docker", AssetKey = "docker-01",
                    RuntimeKind = RuntimeEnvironmentKind.Standalone
                },
                new ApplicationConfig
                {
                    Id = "app1", Name = "order", Type = "java", AssetKey = "order-service",
                    RuntimeKind = RuntimeEnvironmentKind.DockerApplication,
                    RuntimeDockerAssetKey = "docker-01"
                }
            }
        };

        var result = AssetRuntimeValidator.Validate(config);

        Assert.Contains(result.Issues, i => i.Code == "docker_runtime_container_required");
    }

    [Fact]
    public void Runtime_cycle_is_reported()
    {
        var config = new AppConfig
        {
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "a", Name = "a", Type = "java", AssetKey = "a",
                    RuntimeKind = RuntimeEnvironmentKind.DockerApplication,
                    RuntimeDockerAssetKey = "b", RuntimeContainerName = "a"
                },
                new ApplicationConfig
                {
                    Id = "b", Name = "b", Type = "java", AssetKey = "b",
                    RuntimeKind = RuntimeEnvironmentKind.DockerApplication,
                    RuntimeDockerAssetKey = "a", RuntimeContainerName = "b"
                }
            }
        };

        var result = AssetRuntimeValidator.Validate(config);

        Assert.Contains(result.Issues, i => i.Code == "runtime_cycle");
    }

    [Fact]
    public void Invalid_url_and_unspecified_runtime_are_reported()
    {
        var config = new AppConfig
        {
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "app1", Name = "order", Type = "java", AssetKey = "order-service",
                    RuntimeKind = RuntimeEnvironmentKind.Unspecified,
                    Urls = new[] { new UrlPattern { Url = "not a url" } }
                }
            }
        };

        var result = AssetRuntimeValidator.Validate(config);

        Assert.Contains(result.Issues, i => i.Code == "url_invalid");
        Assert.Contains(result.Issues, i => i.Code == "runtime_not_configured");
    }

    private static SshServerConfig Server(string key, string name) => new()
    {
        Id = key + "-id",
        Name = name,
        Host = "10.0.0.1",
        AssetKey = key
    };
}
