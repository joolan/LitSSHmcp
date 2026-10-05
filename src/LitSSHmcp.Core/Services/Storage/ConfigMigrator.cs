using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// 配置结构迁移。当前 schemaVersion = 1（初始版本）。
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

        if (config.Agent.Memory == null)
        {
            config.Agent.Memory = new AgentMemoryConfig();
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
