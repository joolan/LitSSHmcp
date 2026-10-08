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

    [Fact]
    public void V2_single_model_provider_is_split_into_models()
    {
        // v2 配置：一个 provider 只有一个 model 字段（反序列化落到遗留 Model 上）
        var config = new AppConfig
        {
            SchemaVersion = 2,
            Agent = new AgentConfig
            {
                ActiveProviderId = "p1",
                Providers = new[]
                {
                    new AgentProviderGroupConfig
                    {
                        Id = "p1",
                        Name = "DeepSeek",
                        Endpoint = "https://api.deepseek.com/v1",
                        ApiKey = "k",
                        Model = "deepseek-chat",
                        SupportsVision = true
                    },
                    new AgentProviderGroupConfig
                    {
                        Id = "p2",
                        Name = "无模型",
                        Endpoint = "https://x/v1",
                        Model = "   "   // 旧配置里没填模型名 → 不生成模型条目
                    }
                }
            }
        };

        var changed = ConfigMigrator.Migrate(config);

        Assert.True(changed);
        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Equal("p1", config.Agent.ActiveProviderId);

        var g1 = config.Agent.Providers[0];
        Assert.Null(g1.Model);
        var m1 = Assert.Single(g1.Models);
        Assert.Equal("p1", m1.Id);            // 模型 Id 沿用旧 provider Id，保证已保存的选中继续命中
        Assert.Equal("deepseek-chat", m1.Name);
        Assert.True(m1.Enabled);
        Assert.True(m1.SupportsVision);       // 厂家级视觉标记迁移到模型上

        Assert.Empty(config.Agent.Providers[1].Models);

        // 幂等：再迁一次不再修改
        Assert.False(ConfigMigrator.Migrate(config));
    }
}

public class ProviderFlattenTests
{
    [Fact]
    public void Flatten_projects_models_and_filters_disabled_entries()
    {
        var groups = new[]
        {
            new AgentProviderGroupConfig
            {
                Name = "A", Endpoint = "https://x/v1", ApiKey = "k", Enabled = true,
                Models = new[]
                {
                    new AgentModelConfig { Id = "m1", Name = "mm1", Enabled = true, SupportsVision = true },
                    new AgentModelConfig { Id = "m2", Name = "mm2", Enabled = false }
                }
            },
            new AgentProviderGroupConfig   // 无 Endpoint → 整组过滤
            {
                Name = "B", Endpoint = " ", Enabled = true,
                Models = new[] { new AgentModelConfig { Id = "m3", Name = "mm3" } }
            },
            new AgentProviderGroupConfig   // 厂家停用 → 整组过滤
            {
                Name = "C", Endpoint = "https://y/v1", Enabled = false,
                Models = new[] { new AgentModelConfig { Id = "m4", Name = "mm4" } }
            },
            new AgentProviderGroupConfig   // 空模型名 → 过滤
            {
                Name = "D", Endpoint = "https://z/v1", Enabled = true,
                Models = new[] { new AgentModelConfig { Id = "m5", Name = " " } }
            }
        };

        var flat = AgentProviderGroupConfig.Flatten(groups);

        var only = Assert.Single(flat);
        Assert.Equal("m1", only.Id);
        Assert.Equal("mm1", only.Model);
        Assert.Equal("A", only.GroupName);
        Assert.Equal("A / mm1", only.DisplayName);
        Assert.Equal("https://x/v1", only.Endpoint);
        Assert.Equal("k", only.ApiKey);
        Assert.True(only.SupportsVision);
        Assert.True(only.Enabled);
    }

    [Fact]
    public void Flatten_null_is_empty()
    {
        Assert.Empty(AgentProviderGroupConfig.Flatten(null));
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
