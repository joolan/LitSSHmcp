using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

public class DuplicateAssetDetectorTests
{
    [Fact]
    public void FindAll_groups_ssh_profiles_with_same_host_and_port()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web-public", Host = "10.0.0.1", Port = 22, Username = "ops" },
                new SshServerConfig { Id = "s2", Name = "web-admin", Host = "10.0.0.1", Port = 22, Username = "admin" },
                new SshServerConfig { Id = "s3", Name = "web-other", Host = "10.0.0.2", Port = 22 }
            }
        };

        var groups = DuplicateAssetDetector.FindAll(config);

        var group = Assert.Single(groups, g => g.AssetType == "ssh");
        Assert.Equal(2, group.Items.Length);
        Assert.DoesNotContain(group.Items, i => i.Id == "s3");
    }

    [Fact]
    public void FindAll_groups_ssh_profiles_when_public_and_internal_hosts_overlap()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig
                {
                    Id = "s1", Name = "web-public", Host = "1.2.3.4", Port = 22,
                    Aliases = new[] { "10.0.0.5" }
                },
                new SshServerConfig { Id = "s2", Name = "web-internal", Host = "10.0.0.5", Port = 22 }
            }
        };

        var groups = DuplicateAssetDetector.FindAll(config);

        var group = Assert.Single(groups, g => g.AssetType == "ssh");
        Assert.Equal(2, group.Items.Length);
    }

    [Fact]
    public void FindAll_does_not_group_ssh_profiles_with_different_ports()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "a", Host = "10.0.0.1", Port = 22 },
                new SshServerConfig { Id = "s2", Name = "b", Host = "10.0.0.1", Port = 2222 }
            }
        };

        var groups = DuplicateAssetDetector.FindAll(config);

        Assert.DoesNotContain(groups, g => g.AssetType == "ssh");
    }

    [Fact]
    public void FindAll_groups_datasource_profiles_for_same_instance_and_access_mode()
    {
        var config = new AppConfig
        {
            DataSources = new[]
            {
                new DataSourceConfig
                {
                    Id = "ds1", Name = "order-direct", Type = "mysql",
                    Host = "10.0.0.20", Port = 3306, AccessMode = AccessMode.Direct
                },
                new DataSourceConfig
                {
                    Id = "ds2", Name = "order-tunnel", Type = "mysql",
                    Host = "10.0.0.20", Port = 3306, AccessMode = AccessMode.SshTunnel,
                    TunnelServerId = "s1"
                },
                new DataSourceConfig
                {
                    Id = "ds3", Name = "cache", Type = "redis",
                    Host = "10.0.0.20", Port = 3306
                }
            }
        };

        var groups = DuplicateAssetDetector.FindAll(config);

        var group = Assert.Single(groups, g => g.AssetType == "datasource");
        Assert.Equal(2, group.Items.Length);
        Assert.DoesNotContain(group.Items, i => i.Id == "ds3");
    }

    [Fact]
    public void FindAll_does_not_group_localhost_datasources_with_different_tunnel_servers()
    {
        var config = new AppConfig
        {
            DataSources = new[]
            {
                new DataSourceConfig
                {
                    Id = "ds1", Name = "a-mysql", Type = "mysql", Host = "127.0.0.1", Port = 3306,
                    AccessMode = AccessMode.SshTunnel, TunnelServerId = "s1"
                },
                new DataSourceConfig
                {
                    Id = "ds2", Name = "b-mysql", Type = "mysql", Host = "127.0.0.1", Port = 3306,
                    AccessMode = AccessMode.SshTunnel, TunnelServerId = "s2"
                }
            }
        };

        var groups = DuplicateAssetDetector.FindAll(config);

        Assert.DoesNotContain(groups, g => g.AssetType == "datasource");
    }

    [Fact]
    public void FindAll_does_not_group_localhost_ssh_profiles_with_different_jump_servers()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig
                {
                    Id = "s1", Name = "a", Host = "127.0.0.1", Port = 22, ProxyJumpServerId = "jump1"
                },
                new SshServerConfig
                {
                    Id = "s2", Name = "b", Host = "127.0.0.1", Port = 22, ProxyJumpServerId = "jump2"
                }
            }
        };

        var groups = DuplicateAssetDetector.FindAll(config);

        Assert.DoesNotContain(groups, g => g.AssetType == "ssh");
    }

    [Fact]
    public void FindSshMatches_excludes_the_profile_being_edited()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "a", Host = "10.0.0.1", Port = 22 },
                new SshServerConfig { Id = "s2", Name = "b", Host = "10.0.0.1", Port = 22 }
            }
        };

        var matches = DuplicateAssetDetector.FindSshMatches(config, config.Servers[0]);

        Assert.Single(matches);
        Assert.Equal("s2", matches[0].Id);
    }
}
