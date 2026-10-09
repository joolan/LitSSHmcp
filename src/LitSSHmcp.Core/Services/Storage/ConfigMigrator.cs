using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// 配置结构迁移。当前 schemaVersion = 3（2→3 大模型接入改为「厂家下挂多模型」，v2 单模型 provider 拆出 models）。
/// 后续结构变更时在此追加迁移步骤并提升 AppConfig.CurrentSchemaVersion。
/// </summary>
public static class ConfigMigrator
{
    /// <returns>配置是否被修改（需要回写）。</returns>
    public static bool Migrate(AppConfig config)
    {
        var changed = false;

        // 保证安全配置各节非空（JSON 中显式为 null 时反序列化会置空）
        if (config.Security == null)
        {
            config.Security = new SecurityConfig();
            changed = true;
        }

        if (config.Security.CommandFilter == null)
        {
            config.Security.CommandFilter = new CommandFilterConfig();
            changed = true;
        }

        if (config.Security.SqlFilter == null)
        {
            config.Security.SqlFilter = new SqlFilterConfig();
            changed = true;
        }

        if (config.Security.FileTransfer == null)
        {
            config.Security.FileTransfer = new FileTransferConfig();
            changed = true;
        }

        if (config.Security.SshHostKey == null)
        {
            config.Security.SshHostKey = new SshHostKeyConfig();
            changed = true;
        }

        if (config.Security.Discovery == null)
        {
            config.Security.Discovery = new DiscoveryConfig();
            changed = true;
        }

        if (config.Security.Logs == null)
        {
            config.Security.Logs = new LogConfig();
            changed = true;
        }

        if (config.Security.Masking == null)
        {
            config.Security.Masking = new MaskingConfig();
            changed = true;
        }

        if (config.Security.Limits == null)
        {
            config.Security.Limits = new LimitsConfig();
            changed = true;
        }

        if (config.Security.Audit == null)
        {
            config.Security.Audit = new AuditConfig();
            changed = true;
        }

        if (config.Security.Approval == null)
        {
            config.Security.Approval = new ApprovalConfig();
            changed = true;
        }

        if (config.Snapshot == null)
        {
            config.Snapshot = new SnapshotConfig();
            changed = true;
        }

        if (config.Agent == null)
        {
            config.Agent = new AgentConfig();
            changed = true;
        }

        if (config.Ui == null)
        {
            config.Ui = new UiConfig();
            changed = true;
        }

        if (config.Agent.Memory == null)
        {
            config.Agent.Memory = new AgentMemoryConfig();
            changed = true;
        }

        // 默认值刷新：旧默认 contextTokenLimit=24000 偏小，随模型窗口普遍增大升级为 96000。
        // 仅在仍是旧默认值时替换，用户手动设过其它值则保留。
        if (config.Agent.ContextTokenLimit == 24000)
        {
            config.Agent.ContextTokenLimit = 96000;
            changed = true;
        }

        if (config.SyncTasks == null)
        {
            config.SyncTasks = Array.Empty<SyncTaskConfig>();
            changed = true;
        }

        // v2→v3：大模型接入从「一个 provider = 一个模型」改为「一个厂家下挂多个模型」。
        // 旧 JSON 的 model 字段反序列化到遗留 Model 上（此时 models 为空），在此转成单模型列表；
        // 模型 Id 沿用旧 provider Id，保证已保存的 activeProviderId / 会话 ProviderId 继续命中。
        if (config.Agent != null && config.Agent.Providers != null)
        {
            foreach (var g in config.Agent.Providers)
            {
                if (g == null)
                    continue;

                if ((g.Models == null || g.Models.Length == 0) && !string.IsNullOrWhiteSpace(g.Model))
                {
                    g.Models = new[]
                    {
                        new AgentModelConfig
                        {
                            Id = g.Id,
                            Name = g.Model.Trim(),
                            Enabled = true,
                            SupportsVision = g.SupportsVision
                        }
                    };
                    changed = true;
                }
                else if (g.Models == null)
                {
                    g.Models = Array.Empty<AgentModelConfig>();
                    changed = true;
                }

                if (g.Model != null)
                {
                    g.Model = null;   // 已转入 models，不再写出遗留字段
                    changed = true;
                }
            }
        }
        else if (config.Agent != null && config.Agent.Providers == null)
        {
            config.Agent.Providers = Array.Empty<AgentProviderGroupConfig>();
            changed = true;
        }

        // 旧版本（无 schemaVersion）视为 0，补写为当前版本
        if (config.SchemaVersion < AppConfig.CurrentSchemaVersion)
        {
            config.SchemaVersion = AppConfig.CurrentSchemaVersion;
            changed = true;
        }

        return changed;
    }
}
