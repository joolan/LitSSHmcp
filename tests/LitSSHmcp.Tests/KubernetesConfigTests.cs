using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Kubernetes;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

public class KubernetesConfigTests
{
    [Fact]
    public void Environment_and_provider_values_are_normalized()
    {
        Assert.Equal(KubernetesEnvironmentKinds.Production,
            KubernetesEnvironmentKinds.Normalize(" PRODUCTION "));
        Assert.Equal(KubernetesEnvironmentKinds.Custom,
            KubernetesEnvironmentKinds.Normalize("unknown"));
        Assert.Equal(KubernetesProviders.Aliyun,
            KubernetesProviders.Normalize("Aliyun"));
        Assert.Equal(KubernetesProviders.K3s,
            KubernetesProviders.Normalize(" K3S "));
        Assert.Equal(KubernetesProviders.Generic,
            KubernetesProviders.Normalize("unknown"));
    }

    [Fact]
    public void K3s_distribution_is_detected_from_server_version()
    {
        Assert.Equal("K3s", KubernetesService.DetectDistribution(
            "{\"serverVersion\":{\"gitVersion\":\"v1.30.4+k3s1\"}}"));
        Assert.Equal("Kubernetes", KubernetesService.DetectDistribution(
            "{\"serverVersion\":{\"gitVersion\":\"v1.30.4\"}}"));
    }

    [Fact]
    public void Remote_connection_uses_ssh_access_display()
    {
        var connection = new KubernetesConnectionConfig
        {
            Name = "ack-prod",
            EnvironmentKind = KubernetesEnvironmentKinds.Production,
            Provider = KubernetesProviders.Aliyun,
            AccessMode = KubernetesAccessModes.RemoteSsh
        };

        Assert.True(connection.IsRemote);
        Assert.True(connection.IsHighRisk);
        Assert.Equal("生产", KubernetesEnvironmentKinds.Label(connection.EnvironmentKind));
        Assert.Equal("阿里云 ACK", connection.ProviderDisplay);
        Assert.Contains("SSH", connection.AccessDisplay);
    }

    [Fact]
    public void Migration_fills_kubernetes_section_and_defaults()
    {
        var config = new AppConfig
        {
            SchemaVersion = AppConfig.CurrentSchemaVersion,
            Kubernetes = new KubernetesConfig
            {
                Connections = new[]
                {
                    new KubernetesConnectionConfig
                    {
                        Name = "test",
                        KubectlPath = string.Empty,
                        RemoteKubectlPath = string.Empty,
                        TimeoutSeconds = 0
                    }
                }
            }
        };

        ConfigMigrator.Migrate(config);

        var connection = Assert.Single(config.Kubernetes.Connections);
        Assert.Equal("kubectl", connection.KubectlPath);
        Assert.Equal("kubectl", connection.RemoteKubectlPath);
        Assert.Equal(30, connection.TimeoutSeconds);
    }
}
