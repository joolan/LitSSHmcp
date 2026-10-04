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

    /// <summary>服务器快照（ssh_snapshot_get / ssh_snapshot_refresh）的行为配置。</summary>
    public SnapshotConfig Snapshot { get; set; } = new();
}

/// <summary>
/// 服务器快照配置。快照是"整机态势的可持久化采集结果"：
/// 资源使用/端口↔进程↔服务三元组/nginx证书/systemd健康，按服务器落库存历史。
/// </summary>
public class SnapshotConfig
{
    /// <summary>
    /// 采集是否自动提权(sudo)执行只读探测命令。开启后"端口↔进程↔服务"三元组能看到
    /// root 进程的属主与 cgroup 服务名；仅当该服务器配置了 SudoType 时才生效，
    /// 否则自动降级（相关字段缺失并标注 degraded）。逐次采集命令均为内置固定只读命令，
    /// 不要求逐次审批；把此项设为 false 可完全禁止快照提权。
    /// </summary>
    public bool UseSudo { get; set; } = true;

    /// <summary>每台服务器保留的最近快照份数（含其事件级联清理）；0 = 不限制。</summary>
    public int RetentionPerServer { get; set; } = 30;

    /// <summary>单次快照整体超时（秒），超时后剩余采集器标记为跳过并落库为失败。</summary>
    public int TimeoutSeconds { get; set; } = 180;
}

public class SecurityConfig
{
    /// <summary>全局 MCP 开关：false 时拒绝所有工具调用（安全设置中可切换，热生效、无需重启）。</summary>
    public bool Enabled { get; set; } = true;

    public CommandFilterConfig CommandFilter { get; set; } = new();
    public SqlFilterConfig SqlFilter { get; set; } = new();
    public FileTransferConfig FileTransfer { get; set; } = new();
    public SshHostKeyConfig SshHostKey { get; set; } = new();
    public DiscoveryConfig Discovery { get; set; } = new();

    /// <summary>日志读取工具(log_tail / log_grep)的允许路径与行数上限。</summary>
    public LogConfig Logs { get; set; } = new();

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
    /// 审批模式：
    ///  - <c>manual</c>(默认)：所有触发审批的敏感操作都需要人工处理（弹窗或带外 CLI）；
    ///  - <c>auto-approve</c>(危险)：所有触发审批的操作**自动放行**（不弹窗/不等带外），仅建议在受控/演示/自助环境使用；
    ///  - <c>auto-reject</c>：所有触发审批的操作**直接拒绝**（无人值守时默认拒绝的收敛策略）。
    /// 注意：仅影响"需人工确认"的敏感操作；被命令过滤器判为 <c>Blocked</c> 的仍然是硬拒绝，不受此开关影响。
    /// </summary>
    public string Mode { get; set; } = "manual";

    /// <summary>
    /// 审批通道(按顺序同时启用, 首个给出结论者生效):
    /// desktop=本机桌面弹窗; cli=带外 CLI/IPC(操作员用 litssh approve/deny 决定, 适配无桌面/headless)。
    /// 默认 desktop+cli：无桌面环境下 desktop 必然失败，只配 desktop 会导致这类环境 100% 拒绝。
    /// </summary>
    public string[] Channels { get; set; } = { "desktop", "cli" };

    /// <summary>
    /// 授权确认无操作超时（秒），超时自动拒绝并返回 <c>status=approval_timeout</c>；
    /// 0 表示不超时（按 300 秒兜底）。默认 45s：要短于多数 MCP 客户端的工具超时，
    /// 否则"等满 120s 再失败"会让整次调用被客户端判为超时。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 45;

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
    public string[] AllowedSearchPaths { get; set; } = { "/opt", "/home", "/srv", "/app", "/data", "/etc/nginx" };

    /// <summary>
    /// 自动发现是否用提权(sudo)执行只读探测命令。开启后 <c>ss -ltnp</c> 能看到 root 服务的进程名，
    /// 从而拿到精确监听端口（如宝塔启动的 nginx）；仅当该服务器配置了 SudoType 时才生效。
    /// 探测命令均为只读；提权密码由服务端注入、不会暴露给 AI。
    /// </summary>
    public bool UseSudo { get; set; }
}

/// <summary>
/// 日志读取工具(log_tail / log_grep)的约束：只允许读取白名单根目录下的日志文件，
/// 避免模型读取 /etc/shadow 等敏感文件；单次返回行数也有上限。
/// </summary>
public class LogConfig
{
    public string[] AllowedPaths { get; set; } = { "/var/log", "/opt", "/srv", "/app", "/data", "/home" };

    /// <summary>单次最多返回的行数（tool 参数上限的兜底）。</summary>
    public int MaxLines { get; set; } = 2000;
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