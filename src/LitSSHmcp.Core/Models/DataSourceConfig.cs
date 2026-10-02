namespace LitSSHmcp.Core.Models;

public enum AccessMode
{
    Direct,
    SshTunnel
}

/// <summary>数据源写操作审批策略。</summary>
public enum WriteApprovalMode
{
    /// <summary>一律弹出桌面确认（默认，最安全）。</summary>
    Always,
    /// <summary>受信任目标：跳过桌面确认（仍受硬拦截规则约束），用于开发库等低风险场景。</summary>
    AutoApprove
}

public class DataSourceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "mysql";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 3306;
    public string Username { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string? DefaultDatabase { get; set; }
    public AccessMode AccessMode { get; set; } = AccessMode.Direct;
    public string? TunnelServerId { get; set; }
    public string? Description { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // —— 治理（可选）：每数据源的限额 / 只读 / 写审批策略 ——

    /// <summary>单次查询最大返回行数（留空 = 使用工具默认与上限）。</summary>
    public int? MaxRows { get; set; }

    /// <summary>命令超时（秒，留空 = 驱动默认）。</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>只读数据源：拒绝该数据源上的所有写操作（mysql_execute / redis_execute）。</summary>
    public bool ReadOnly { get; set; }

    /// <summary>写操作审批策略：Always=一律桌面确认（默认）；AutoApprove=跳过桌面确认（仍受硬拦截规则）。</summary>
    public WriteApprovalMode WriteApproval { get; set; } = WriteApprovalMode.Always;
}

public class ApplicationConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "java";
    public int? Port { get; set; }
    public string? Host { get; set; }
    public string? Description { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>Docker 容器名（type=docker 时用于把应用关联到容器，便于 AI 运维；拓扑发现据此匹配并自动建立 runsOn 关系）。</summary>
    public string? ContainerName { get; set; }

    /// <summary>应用路径（可选）：部署目录 / jar / 可执行文件路径，便于定位日志与排查。自动发现时会尝试推断并预填。</summary>
    public string? Path { get; set; }

    /// <summary>
    /// 该应用的日志文件路径（可多条）。供 log_tail/log_grep 在只传 appId 时解析路径；
    /// 仍受 security.logs.allowedPaths 白名单约束。
    /// </summary>
    public string[] LogPaths { get; set; } = Array.Empty<string>();
}

public class RelationConfig
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public static class AssetNode
{
    public const string SshPrefix = "ssh:";
    public const string DsPrefix = "ds:";
    public const string AppPrefix = "app:";
    public const string MqPrefix = "mq:";

    public static string Ssh(string serverId) => SshPrefix + serverId;
    public static string Ds(string datasourceId) => DsPrefix + datasourceId;
    public static string App(string applicationId) => AppPrefix + applicationId;
    public static string Mq(string endpoint) => MqPrefix + endpoint;
}
