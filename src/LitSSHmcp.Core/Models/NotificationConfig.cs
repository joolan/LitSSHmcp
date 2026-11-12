namespace LitSSHmcp.Core.Models;

/// <summary>通知消息类型（生产者与路由规则共用的稳定标识）。</summary>
public static class NotificationTypes
{
    public const string Generic = "app.general";
    public const string SnapshotSucceeded = "snapshot.succeeded";
    public const string SnapshotFailed = "snapshot.failed";
    public const string HealthWarning = "health.warning";
    public const string HealthCritical = "health.critical";
    public const string AiTaskSucceeded = "ai_task.succeeded";
    public const string AiTaskFailed = "ai_task.failed";
    public const string BackupSucceeded = "backup.succeeded";
    public const string BackupFailed = "backup.failed";
    public const string JenkinsBuildCompleted = "jenkins.build.completed";
    public const string JenkinsBuildFailed = "jenkins.build.failed";
    public const string SecurityAlert = "security.alert";
    public const string ApprovalPending = "approval.pending";
    public const string ApprovalTimeout = "approval.timeout";
    public const string TriggerFailed = "trigger.failed";
    public const string SyncFailed = "sync.failed";
    public const string WebLoginFailed = "web.login_failed";
    public const string SystemError = "system.error";
}

public sealed class NotificationConfig
{
    public NotificationChannelConfig[] Channels { get; set; } = Array.Empty<NotificationChannelConfig>();
    public NotificationRouteConfig[] Routes { get; set; } = Array.Empty<NotificationRouteConfig>();
}

public sealed class NotificationChannelConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>app / desktop / dingtalk / wecom / feishu / webhook</summary>
    public string Type { get; set; } = "app";
    public bool Enabled { get; set; } = true;

    /// <summary>Webhook 地址（非 app 渠道使用）。</summary>
    public string Url { get; set; } = string.Empty;
    public string? Secret { get; set; }

    /// <summary>generic / dingtalk / wecom / feishu</summary>
    public string Template { get; set; } = "generic";
    public int TimeoutSeconds { get; set; } = 10;
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class NotificationRouteConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>支持通配符：ai_task.* / snapshot.*。</summary>
    public string[] Types { get; set; } = Array.Empty<string>();
    public string[] Channels { get; set; } = Array.Empty<string>();
    public string MinSeverity { get; set; } = "info";

    /// <summary>同一路由/渠道在时间窗内的最大发送数；0=不限。</summary>
    public int RateLimitCount { get; set; }
    public int RateLimitWindowSeconds { get; set; } = 300;

    /// <summary>相同 DedupeKey 的去重窗口；0=不去重。</summary>
    public int DedupeSeconds { get; set; }
}
