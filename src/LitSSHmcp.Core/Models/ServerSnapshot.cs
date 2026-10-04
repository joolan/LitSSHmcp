namespace LitSSHmcp.Core.Models;

/// <summary>快照生命周期状态。落库为同名小写字符串，读取用忽略大小写的枚举解析（兼容旧值）。</summary>
public enum SnapshotStatus
{
    /// <summary>采集中（进程崩溃/重启后由 Store 初始化时改判为 Failed/interrupted）。</summary>
    Running,
    Succeeded,
    Failed
}

/// <summary>
/// 一份服务器快照（整机态势的持久化采集结果）。
/// DataJson 结构: { collectorVersion, sections: { [collectorName]: { status, durationMs, error, degraded, data } } }。
/// </summary>
public class ServerSnapshot
{
    public long Id { get; set; }
    public string ServerId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;

    /// <summary>ISO-8601 往返格式（"O"）文本，保证字典序 == 时间序。</summary>
    public string CreatedAt { get; set; } = string.Empty;
    public string? CompletedAt { get; set; }

    public SnapshotStatus Status { get; set; }
    public double DurationMs { get; set; }
    public string? Error { get; set; }

    /// <summary>本次快照实际使用的提权机制（direct/sudo/su/auto:sudo/auto:su/null=未提权）。</summary>
    public string? Escalation { get; set; }

    /// <summary>采集器集合版本；新增/调整采集维度时递增，趋势解析按此判断口径。</summary>
    public int CollectorVersion { get; set; }

    /// <summary>完整采集数据（JSON 文本，见类注释结构）。</summary>
    public string? DataJson { get; set; }
}

/// <summary>快照事件（append-only 流水）：started / collector_started / collector_completed / collector_failed / collector_skipped / completed / failed。</summary>
public class SnapshotEvent
{
    public long Id { get; set; }
    public long SnapshotId { get; set; }
    public string ServerId { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;

    /// <summary>关联采集器名（resource/portmap/nginx_tls/systemd）；非采集器事件为 null。</summary>
    public string? Collector { get; set; }
    public string? Message { get; set; }
}
