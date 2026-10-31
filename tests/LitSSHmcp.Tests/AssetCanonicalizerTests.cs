using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

public class AssetCanonicalizerTests
{
    [Fact]
    public void Build_groups_ssh_profiles_by_asset_key()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig
                {
                    Id = "s1", Name = "web-public", Host = "1.2.3.4", Port = 22,
                    AssetKey = "prod-web", CreatedAt = new DateTime(2026, 1, 1)
                },
                new SshServerConfig
                {
                    Id = "s2", Name = "web-internal", Host = "10.0.0.5", Port = 22,
                    AssetKey = "prod-web", CreatedAt = new DateTime(2026, 1, 2)
                },
                new SshServerConfig { Id = "s3", Name = "other", Host = "10.0.0.9", Port = 22 }
            }
        };

        var canonical = AssetCanonicalizer.Build(config);

        Assert.Equal("ssh:prod-web", canonical.ResolveNodeId("ssh:s1"));
        Assert.Equal("ssh:prod-web", canonical.ResolveNodeId("ssh:s2"));
        Assert.Equal("ssh:s3", canonical.ResolveNodeId("ssh:s3"));

        var asset = canonical.Assets["ssh:prod-web"];
        Assert.Equal(2, asset.Profiles.Count);
        Assert.Equal("prod-web", asset.AssetKey);
    }

    [Fact]
    public void Build_groups_ssh_profiles_by_shared_strong_identity()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "a", Host = "1.2.3.4", Port = 22, CreatedAt = new DateTime(2026, 1, 1) },
                new SshServerConfig { Id = "s2", Name = "b", Host = "10.0.0.5", Port = 22, CreatedAt = new DateTime(2026, 1, 2) }
            }
        };

        var identities = new[]
        {
            new AssetIdentityRecord
            {
                AssetId = "s1", AssetType = "ssh",
                IdentityKind = AssetIdentityKinds.SshMachineId, IdentityValue = "machine-1"
            },
            new AssetIdentityRecord
            {
                AssetId = "s2", AssetType = "ssh",
                IdentityKind = AssetIdentityKinds.SshMachineId, IdentityValue = "machine-1"
            }
        };

        var canonical = AssetCanonicalizer.Build(config, identities);

        Assert.Equal(canonical.ResolveNodeId("ssh:s1"), canonical.ResolveNodeId("ssh:s2"));
        Assert.Equal(2, canonical.Assets.Values.Single().Profiles.Count);
    }

    [Fact]
    public void Build_groups_datasource_profiles_by_asset_key()
    {
        var config = new AppConfig
        {
            DataSources = new[]
            {
                new DataSourceConfig
                {
                    Id = "ds1", Name = "order-direct", Type = "mysql",
                    Host = "10.0.0.20", Port = 3306, AssetKey = "prod-mysql",
                    CreatedAt = new DateTime(2026, 1, 1)
                },
                new DataSourceConfig
                {
                    Id = "ds2", Name = "order-tunnel", Type = "mysql",
                    Host = "10.0.0.20", Port = 3306, AssetKey = "prod-mysql",
                    AccessMode = AccessMode.SshTunnel, TunnelServerId = "jump",
                    CreatedAt = new DateTime(2026, 1, 2)
                }
            }
        };

        var canonical = AssetCanonicalizer.Build(config);

        Assert.Equal("ds:prod-mysql", canonical.ResolveNodeId("ds:ds1"));
        Assert.Equal("ds:prod-mysql", canonical.ResolveNodeId("ds:ds2"));
        Assert.Equal(2, canonical.Assets["ds:prod-mysql"].Profiles.Count);
    }

    [Fact]
    public void Build_does_not_group_assets_with_different_asset_keys()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "a", Host = "10.0.0.1", Port = 22, AssetKey = "a" },
                new SshServerConfig { Id = "s2", Name = "b", Host = "10.0.0.1", Port = 22, AssetKey = "b" }
            }
        };

        var canonical = AssetCanonicalizer.Build(config);

        Assert.NotEqual(canonical.ResolveNodeId("ssh:s1"), canonical.ResolveNodeId("ssh:s2"));
    }
}
