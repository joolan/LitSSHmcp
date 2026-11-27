using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// 配置结构迁移。当前 schemaVersion = 9（2→3 大模型接入改为「厂家下挂多模型」；
/// v9 把记忆召回阈值旧默认 0.15 升级为 0.45）。
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

        config.Notifications ??= new NotificationConfig();

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

        if (config.Agent.LocalTerminal == null)
        {
            config.Agent.LocalTerminal = new LocalTerminalConfig();
            changed = true;
        }
        else if (AutoApprovedCommandRule.Migrate(
                     config.Agent.LocalTerminal.AutoApprovedCommands, out var autoApproved))
        {
            config.Agent.LocalTerminal.AutoApprovedCommands = autoApproved;
            changed = true;
        }

        // 默认值刷新：旧默认 contextTokenLimit=24000 偏小，随模型窗口普遍增大升级为 96000。
        // 加 schemaVersion 门控保证只在旧版本配置上做一次；否则用户在 UI 里主动设回 24000
        // 会在每次加载时被永久覆盖（该字段是 UI 可配的可变项，不能按值劫持）。
        if (config.SchemaVersion < 9 && config.Agent.ContextTokenLimit == 24000)
        {
            config.Agent.ContextTokenLimit = 96000;
            changed = true;
        }

        // v8→v9：长期记忆召回阈值旧默认 0.15 在 qwen3(1024 维) 向量下几乎等于不过滤
        // （库内两两余弦中位 0.446、99.9% 配对 > 0.15），重校准为 0.45。
        // 一次性迁移：仅「旧版本配置 + 值恰为旧默认」时升级并写回 config.json；
        // 版本提升后用户可自由把阈值设为任意值（含 0.15），运行时不再覆盖。
        // 注意：不能用「运行时按值替换」实现——那会把用户主动设回 0.15 的配置永久劫持，
        // 且只改内存不写回文件，造成 config.json 显示 0.15、实际生效 0.45 的不一致。
        if (config.SchemaVersion < 9 && config.Agent.Memory is { } memory &&
            System.Math.Abs(memory.RecallThreshold - AgentMemoryConfig.LegacyRecallThreshold) < 0.0001)
        {
            memory.RecallThreshold = AgentMemoryConfig.DefaultRecallThreshold;
            changed = true;
        }

        if (config.SyncTasks == null)
        {
            config.SyncTasks = Array.Empty<SyncTaskConfig>();
            changed = true;
        }

        if (config.Triggers == null)
        {
            config.Triggers = Array.Empty<TriggerConfig>();
            changed = true;
        }

        if (config.AppLock == null)
        {
            config.AppLock = new AppLockConfig();
            changed = true;
        }

        if (config.PortForwards == null)
        {
            config.PortForwards = Array.Empty<PortForwardConfig>();
            changed = true;
        }

        // v3→v4：新增独立「远程桌面」功能（RDP / VNC 连接定义）。旧文件无该字段时补空数组。
        if (config.RemoteDesktops == null)
        {
            config.RemoteDesktops = Array.Empty<RemoteDesktopConfig>();
            changed = true;
        }

        // v7→v8：新增 FTP / FTPS 文件传输站点；SFTP 继续复用 SSH 服务器。
        if (config.FileTransferSites == null)
        {
            config.FileTransferSites = Array.Empty<FileTransferSiteConfig>();
            changed = true;
        }

        // v4→v5：新增 Jenkins 多连接配置；连接数量和环境类型均不固定。
        if (config.Jenkins == null)
        {
            config.Jenkins = new JenkinsConfig();
            changed = true;
        }

        if (config.Jenkins.Connections == null)
        {
            config.Jenkins.Connections = Array.Empty<JenkinsConnectionConfig>();
            changed = true;
        }

        if (config.Kubernetes == null)
        {
            config.Kubernetes = new KubernetesConfig();
            changed = true;
        }

        if (config.Kubernetes.Connections == null)
        {
            config.Kubernetes.Connections = Array.Empty<KubernetesConnectionConfig>();
            changed = true;
        }

        foreach (var connection in config.Kubernetes.Connections)
        {
            if (connection is null)
                continue;
            connection.EnvironmentKind = KubernetesEnvironmentKinds.Normalize(connection.EnvironmentKind);
            connection.Provider = KubernetesProviders.Normalize(connection.Provider);
            connection.AccessMode = KubernetesAccessModes.Normalize(connection.AccessMode);
            if (string.IsNullOrWhiteSpace(connection.KubectlPath))
                connection.KubectlPath = "kubectl";
            if (string.IsNullOrWhiteSpace(connection.RemoteKubectlPath))
                connection.RemoteKubectlPath = "kubectl";
            if (connection.TimeoutSeconds <= 0)
                connection.TimeoutSeconds = 30;
        }

        if (config.Applications != null)
        {
            foreach (var application in config.Applications)
            {
                if (application.Urls == null)
                {
                    application.Urls = Array.Empty<UrlPattern>();
                    changed = true;
                }

                if (application.RuntimeKind == RuntimeEnvironmentKind.Unspecified &&
                    !string.IsNullOrWhiteSpace(application.ServerId))
                {
                    var server = (config.Servers ?? Array.Empty<SshServerConfig>())
                        .FirstOrDefault(s => string.Equals(
                            s.Id, application.ServerId, StringComparison.OrdinalIgnoreCase));
                    if (server is not null && !string.IsNullOrWhiteSpace(server.AssetKey))
                    {
                        application.RuntimeKind = RuntimeEnvironmentKind.SshServer;
                        application.RuntimeSshAssetKey = server.AssetKey;
                        changed = true;
                    }
                }
            }
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
        // v7：消息保留上限从 200 提升到 2000。200 对含工具轨迹的会话太小，
        // 一轮 199 个工具步骤就会把本轮 user 提示词挤出保留窗口。
        if (config.Agent is { RetentionMaxMessages: 200 } && config.SchemaVersion < 7)
        {
            config.Agent.RetentionMaxMessages = 2000;
            changed = true;
        }

        if (config.SchemaVersion < AppConfig.CurrentSchemaVersion)
        {
            config.SchemaVersion = AppConfig.CurrentSchemaVersion;
            changed = true;
        }

        return changed;
    }
}
