using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

public class ConfigMigratorTests
{
    [Fact]
    public void Null_security_section_is_filled_and_reported_changed()
    {
        var config = new AppConfig { Security = null! };

        var changed = ConfigMigrator.Migrate(config);

        Assert.True(changed);
        Assert.NotNull(config.Security);
        Assert.NotNull(config.Security.CommandFilter);
        Assert.NotNull(config.Security.SqlFilter);
        Assert.NotNull(config.Security.FileTransfer);
    }

    [Fact]
    public void Null_subsections_are_filled()
    {
        var config = new AppConfig
        {
            Security = new SecurityConfig
            {
                CommandFilter = null!,
                SqlFilter = null!,
                FileTransfer = null!
            }
        };

        Assert.True(ConfigMigrator.Migrate(config));
        Assert.NotNull(config.Security.CommandFilter);
    }

    [Fact]
    public void Current_version_config_is_not_changed()
    {
        var config = new AppConfig { SchemaVersion = AppConfig.CurrentSchemaVersion };

        var changed = ConfigMigrator.Migrate(config);

        Assert.False(changed);
        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
    }

    [Fact]
    public void Missing_version_is_migrated_to_current()
    {
        var config = new AppConfig(); // SchemaVersion 默认 0，模拟旧文件

        var changed = ConfigMigrator.Migrate(config);

        Assert.True(changed);
        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
    }
}

public class AssetNodeTests
{
    [Fact]
    public void Builds_prefixed_node_ids()
    {
        Assert.Equal("ssh:web-01", AssetNode.Ssh("web-01"));
        Assert.Equal("ds:mysql-order-01", AssetNode.Ds("mysql-order-01"));
        Assert.Equal("app:order-service", AssetNode.App("order-service"));
    }
}
