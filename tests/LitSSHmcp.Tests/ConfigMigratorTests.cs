using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
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
    public void Legacy_auto_approved_commands_are_hashed_and_redacted()
    {
        const string command = "curl --password hunter2 https://example";
        var config = new AppConfig
        {
            SchemaVersion = AppConfig.CurrentSchemaVersion,
            Agent = new AgentConfig
            {
                LocalTerminal = new LocalTerminalConfig
                {
                    AutoApprovedCommands = new[] { command }
                }
            }
        };

        Assert.True(ConfigMigrator.Migrate(config));

        var rule = Assert.Single(config.Agent.LocalTerminal.AutoApprovedCommands);
        Assert.StartsWith(AutoApprovedCommandRule.Prefix, rule, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", rule, StringComparison.Ordinal);
        Assert.True(AutoApprovedCommandRule.Matches(command, rule));
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
    public void Legacy_200_message_retention_is_raised_to_2000()
    {
        var config = new AppConfig
        {
            SchemaVersion = 6,
            Agent = new AgentConfig { RetentionMaxMessages = 200 }
        };

        Assert.True(ConfigMigrator.Migrate(config));
        Assert.Equal(2000, config.Agent.RetentionMaxMessages);
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

    [Fact]
    public void Null_application_urls_are_filled()
    {
        var config = new AppConfig
        {
            SchemaVersion = AppConfig.CurrentSchemaVersion,
            Applications = new[]
            {
                new ApplicationConfig { Id = "app1", Name = "order", Urls = null! }
            }
        };

        var changed = ConfigMigrator.Migrate(config);

        Assert.True(changed);
        Assert.NotNull(config.Applications[0].Urls);
        Assert.Empty(config.Applications[0].Urls);
    }

    [Fact]
    public void Legacy_application_server_id_migrates_to_runtime_environment()
    {
        var config = new AppConfig
        {
            SchemaVersion = AppConfig.CurrentSchemaVersion,
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web", Host = "10.0.0.1", AssetKey = "web" }
            },
            Applications = new[]
            {
                new ApplicationConfig { Id = "app1", Name = "order", ServerId = "s1" }
            }
        };

        var changed = ConfigMigrator.Migrate(config);

        Assert.True(changed);
        Assert.Equal(RuntimeEnvironmentKind.SshServer, config.Applications[0].RuntimeKind);
        Assert.Equal("web", config.Applications[0].RuntimeSshAssetKey);
    }

    [Fact]
    public void Null_remote_desktops_is_filled()
    {
        var config = new AppConfig { RemoteDesktops = null!, SchemaVersion = AppConfig.CurrentSchemaVersion };

        var changed = ConfigMigrator.Migrate(config);

        Assert.True(changed);
        Assert.NotNull(config.RemoteDesktops);
        Assert.Empty(config.RemoteDesktops);
    }

    [Fact]
    public void V8_legacy_recall_threshold_015_is_migrated_to_045_once()
    {
        // 旧版 config.json 显式存了旧默认 0.15；加载时一次性升级并（由 ConfigService）写回文件。
        var config = new AppConfig { SchemaVersion = 8 };
        config.Agent.Memory!.RecallThreshold = AgentMemoryConfig.LegacyRecallThreshold;

        Assert.True(ConfigMigrator.Migrate(config));
        Assert.Equal(AgentMemoryConfig.DefaultRecallThreshold, config.Agent.Memory.RecallThreshold);
        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);

        // 幂等：版本已提升，再迁移不再改动任何值。
        Assert.False(ConfigMigrator.Migrate(config));
        Assert.Equal(AgentMemoryConfig.DefaultRecallThreshold, config.Agent.Memory.RecallThreshold);
    }

    [Fact]
    public void Recall_threshold_set_back_to_015_by_user_on_v9_is_never_overridden()
    {
        // 关键回归：阈值是可变配置项。迁移完成后用户主动把阈值设回 0.15，
        // 运行时与配置加载都不得再把它改写成 0.45。
        var config = new AppConfig { SchemaVersion = AppConfig.CurrentSchemaVersion };
        config.Agent.Memory!.RecallThreshold = AgentMemoryConfig.LegacyRecallThreshold;

        Assert.False(ConfigMigrator.Migrate(config));
        Assert.Equal(AgentMemoryConfig.LegacyRecallThreshold, config.Agent.Memory.RecallThreshold);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.6)]
    [InlineData(0.9)]
    public void Custom_recall_threshold_is_always_kept(double threshold)
    {
        var config = new AppConfig { SchemaVersion = 8 };
        config.Agent.Memory!.RecallThreshold = threshold;

        ConfigMigrator.Migrate(config);

        Assert.Equal(threshold, config.Agent.Memory.RecallThreshold);
    }

    [Fact]
    public void Context_token_limit_is_only_migrated_on_old_schema()
    {
        // 同一类 bug：contextTokenLimit 在 UI 里可配，旧逻辑「== 24000 就升级」会永久覆盖用户的设置。
        var old = new AppConfig { SchemaVersion = 8 };
        old.Agent.ContextTokenLimit = 24000;
        Assert.True(ConfigMigrator.Migrate(old));
        Assert.Equal(96000, old.Agent.ContextTokenLimit);

        var current = new AppConfig { SchemaVersion = AppConfig.CurrentSchemaVersion };
        current.Agent.ContextTokenLimit = 24000;   // 用户主动设回旧值
        Assert.False(ConfigMigrator.Migrate(current));
        Assert.Equal(24000, current.Agent.ContextTokenLimit);
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
