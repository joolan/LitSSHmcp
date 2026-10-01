namespace LitSSHmcp.Core.Models;

public class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>配置结构版本。缺失该字段的旧文件反序列化为 0，由 ConfigMigrator 迁移到当前版本。</summary>
    public int SchemaVersion { get; set; }
    public SshServerConfig[] Servers { get; set; } = Array.Empty<SshServerConfig>();
    public DataSourceConfig[] DataSources { get; set; } = Array.Empty<DataSourceConfig>();
    public ApplicationConfig[] Applications { get; set; } = Array.Empty<ApplicationConfig>();
    public RelationConfig[] Relations { get; set; } = Array.Empty<RelationConfig>();
    public SecurityConfig Security { get; set; } = new();

    /// <summary>MCP 工具分组开关（留空 = 全部启用）。</summary>
    public ToolsConfig Tools { get; set; } = new();
}

public class SecurityConfig
{
    public CommandFilterConfig CommandFilter { get; set; } = new();
    public SqlFilterConfig SqlFilter { get; set; } = new();
    public FileTransferConfig FileTransfer { get; set; } = new();
    public SshHostKeyConfig SshHostKey { get; set; } = new();
    public DiscoveryConfig Discovery { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();
    public AuditConfig Audit { get; set; } = new();
    public ApprovalConfig Approval { get; set; } = new();

    /// <summary>查询结果列级脱敏规则。</summary>
    public MaskingConfig Masking { get; set; } = new();
}

/// <summary>结果列级脱敏配置：按列名正则匹配，命中即对单元格值脱敏。</summary>
public class MaskingConfig
{
    public MaskRule[] Rules { get; set; } = Array.Empty<MaskRule>();
}

public class MaskRule
{
    /// <summary>列名匹配（正则，忽略大小写），如 <c>phone|mobile</c>。</summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>脱敏模式：full(全掩码 ***) / email / phone / last4。</summary>
    public string Mode { get; set; } = "full";
}

public class ApprovalConfig
{
    /// <summary>授权确认方式: dialog=自绘置顶对话框(带超时/美化); native=原生置顶 MessageBox(最稳)。</summary>
    public string Style { get; set; } = "dialog";

    /// <summary>
    /// 审批通道(按顺序同时启用, 首个给出决定者生效):
    /// desktop=本机桌面弹窗; cli=带外 CLI/IPC(操作员用 litssh approve/deny 决定, 适配无桌面/headless)。
    /// 默认仅 desktop。
    /// </summary>
    public string[] Channels { get; set; } = { "desktop" };

    /// <summary>授权确认弹窗无操作超时（秒），超时自动拒绝；0 表示不超时（仅 dialog 样式生效）。</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>弹窗是否强制置顶，避免被其它窗口遮挡。</summary>
    public bool TopMost { get; set; } = true;
}

public class AuditConfig
{
    /// <summary>是否记录 SQL 原文；关闭后审计仅记录占位符。</summary>
    public bool StoreSqlText { get; set; } = true;

    /// <summary>是否对 SQL 中的字符串/数字字面量脱敏（替换为 ?）。</summary>
    public bool MaskLiterals { get; set; } = false;

    /// <summary>审计记录保留天数，超期自动清理；0 表示不清理。</summary>
    public int RetentionDays { get; set; } = 90;
}

public enum SshHostKeyMode
{
    /// <summary>首次连接信任并记录指纹，后续指纹变化则拒绝（TOFU）。</summary>
    Tofu,
    /// <summary>只接受已记录的指纹，未记录也拒绝。</summary>
    Strict,
    /// <summary>不校验主机密钥（不推荐，仅兼容需要）。</summary>
    Off
}

public class SshHostKeyConfig
{
    public SshHostKeyMode Mode { get; set; } = SshHostKeyMode.Tofu;
}

public class DiscoveryConfig
{
    public string[] AllowedSearchPaths { get; set; } = { "/opt", "/home", "/srv", "/app", "/data" };
}

public class LimitsConfig
{
    /// <summary>同一目标（服务器/数据源）允许的并发调用数，超出即拒绝。</summary>
    public int MaxConcurrentPerTarget { get; set; } = 3;

    /// <summary>同一目标每分钟允许的调用数，超出即拒绝。</summary>
    public int MaxCallsPerMinutePerTarget { get; set; } = 60;
}

public class FileTransferConfig
{
    public bool Enabled { get; set; } = true;
    public string[] AllowedLocalPaths { get; set; } = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
    public string[] AllowedRemotePaths { get; set; } = new[] { "/home", "/tmp", "/var/log" };
    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB
    public bool RequireApproval { get; set; } = true;
}