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

    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
