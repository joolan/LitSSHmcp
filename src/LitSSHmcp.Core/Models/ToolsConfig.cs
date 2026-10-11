namespace LitSSHmcp.Core.Models;

/// <summary>
/// MCP 工具分组开关：按部署场景裁剪暴露给 AI 的工具，减少上下文占用与误选。
/// 留空 = 全部启用（向后兼容）。分组含义见 docs/TOOLS.md「工具分组」。
/// </summary>
public class ToolsConfig
{
    /// <summary>
    /// 启用的工具分组（大小写不敏感）。可选值: ssh / command / fileTransfer / datasource / mysql / postgres / redis /
    /// docker / service / log / java / topology / app / guide；也接受 "all"（全部）。留空视为全部启用。
    /// </summary>
    public string[] EnabledGroups { get; set; } = Array.Empty<string>();
}

/// <summary>工具分组常量与解析逻辑（与 docs/TOOLS.md 的分组一一对应）。</summary>
public static class ToolGroups
{
    public const string Ssh = "ssh";
    public const string Command = "command";
    public const string FileTransfer = "fileTransfer";
    public const string Datasource = "datasource";
    public const string Mysql = "mysql";
    public const string Postgres = "postgres";
    public const string Redis = "redis";
    public const string Docker = "docker";
    public const string Service = "service";
    public const string Log = "log";
    public const string Java = "java";
    public const string Topology = "topology";
    public const string App = "app";
    public const string Guide = "guide";

    public const string AllKeyword = "all";
    public const string NoneKeyword = "none";

    /// <summary>全部分组（顺序即文档分组顺序）。</summary>
    public static readonly string[] All =
    {
        Ssh, Command, FileTransfer, Datasource, Mysql, Postgres, Redis,
        Docker, Service, Log, Java, Topology, App, Guide
    };

    /// <summary>
    /// 计算实际启用的分组集合：
    /// 未配置 / 空数组 / 含 "all" → 全部；含 "none" → 空；否则取与 <see cref="All"/> 匹配的项
    /// （忽略未知项，大小写不敏感）。
    /// </summary>
    public static HashSet<string> ResolveEnabled(ToolsConfig? config)
    {
        var requested = config?.EnabledGroups;
        if (requested == null || requested.Length == 0 || requested.Any(g => Same(g, AllKeyword)))
            return new HashSet<string>(All, StringComparer.OrdinalIgnoreCase);

        if (requested.Any(g => Same(g, NoneKeyword)))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new HashSet<string>(
            All.Where(a => requested.Any(r => Same(r, a))),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>配置中出现但不在 <see cref="All"/> 里的分组名（用于启动告警，排除 "all"/"none"）。</summary>
    public static string[] UnknownGroups(ToolsConfig? config)
    {
        var requested = config?.EnabledGroups;
        if (requested == null || requested.Length == 0)
            return Array.Empty<string>();

        return requested
            .Where(r => !Same(r, AllKeyword) && !Same(r, NoneKeyword) &&
                        !All.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// 由工具名推断所属分组（键与 <see cref="All"/> 一致），未知返回 null。
    /// 工具命名约定见 docs/TOOLS.md；这是**唯一的工具↔分组映射实现**，Agent 与服务端共用。
    /// </summary>
    public static string? GroupOf(string toolName) => toolName switch
    {
        _ when toolName.StartsWith("mysql_", StringComparison.Ordinal) => Mysql,
        _ when toolName.StartsWith("postgres_", StringComparison.Ordinal) => Postgres,
        _ when toolName.StartsWith("redis_", StringComparison.Ordinal) => Redis,
        _ when toolName.StartsWith("datasource_", StringComparison.Ordinal) => Datasource,
        _ when toolName.StartsWith("docker_", StringComparison.Ordinal) => Docker,
        _ when toolName.StartsWith("service_", StringComparison.Ordinal) => Service,
        _ when toolName.StartsWith("log_", StringComparison.Ordinal) => Log,
        _ when toolName.StartsWith("java_", StringComparison.Ordinal) => Java,
        _ when toolName.StartsWith("topology_", StringComparison.Ordinal) => Topology,
        _ when toolName.StartsWith("app_", StringComparison.Ordinal) => App,
        _ when toolName.StartsWith("mcp_", StringComparison.Ordinal) => Guide,
        "ssh_execute_command" or "ssh_execute_sudo" or "ssh_get_sudo_status" or "ssh_get_command_history" => Command,
        "ssh_upload_file" or "ssh_upload_files" or "ssh_download_file" or "ssh_download_files" or "ssh_list_files" => FileTransfer,
        _ when toolName.StartsWith("ssh_", StringComparison.Ordinal) => Ssh,
        _ => null
    };

    /// <summary>分组的人类可读名（用于 server instructions / 能力边界提示，未收录则原样返回）。</summary>
    public static string Display(string group) => group switch
    {
        Ssh => "服务器(ssh_*)",
        Command => "命令/提权(ssh_execute_*)",
        FileTransfer => "文件传输(ssh_*_file*/*_files)",
        Datasource => "数据源(datasource_*)",
        Mysql => "MySQL(mysql_*)",
        Postgres => "PostgreSQL(postgres_*)",
        Redis => "Redis(redis_*)",
        Docker => "容器(docker_*)",
        Service => "systemd(service_*)",
        Log => "日志(log_*)",
        Java => "JVM(java_*)",
        Topology => "拓扑(topology_*)",
        App => "应用体检(app_*)",
        Guide => "自检/会话(mcp_*)",
        _ => group
    };

    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
