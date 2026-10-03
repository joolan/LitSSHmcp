using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 工具层公共支撑：ID 宽松解析、失败回显可用 ID、输出截断、limit 钳制、审计兜底。
/// 抽成一处是为了让 29 个工具在"传错 ID / 输出过大 / 审计失败"三类问题上表现一致。
/// </summary>
public static class ToolSupport
{
    /// <summary>返回给模型的 SSH 命令输出上限（字符）。超出会置 Truncated=true。</summary>
    public const int MaxOutputChars = 20000;

    /// <summary>写入审计库的输出上限（字符），避免审计库被大输出撑爆。</summary>
    public const int MaxAuditResultChars = 4000;

    /// <summary>历史查询的 limit 上限（此前无上限，limit=-1 会让 SQLite 返回全表）。</summary>
    public const int MaxHistoryLimit = 200;

    public const int DefaultHistoryLimit = 50;

    /// <summary>
    /// 描述必须是编译期常量（C# 特性实参不接受内插字符串），所以这里手动对齐上面两个数值；
    /// 改了 MaxHistoryLimit/DefaultHistoryLimit 记得同步这里。
    /// </summary>
    public const string HistoryLimitDescription = "返回条数, 1-200(默认50), 超出按200处理";

    public static int ClampLimit(int limit) => Math.Clamp(limit, 1, MaxHistoryLimit);

    /// <summary>截断长输出；返回 (文本, 是否被截断, 原始长度)。</summary>
    public static (string Text, bool Truncated, long OriginalLength) Truncate(string? value, int maxChars)
    {
        var s = value ?? string.Empty;
        if (s.Length <= maxChars)
            return (s, false, s.Length);
        return (s[..maxChars] + $"\n...[输出已截断, 原始 {s.Length} 字符, 请用更精确的条件/行数参数重新查询]", true, s.Length);
    }

    /// <summary>审计写入为尽力而为：副作用已发生，审计失败绝不能让工具报错（否则模型重试=重复执行）。</summary>
    public static async Task SafeLogCommandAsync(IAuditLogService audit, CommandAuditLog log)
    {
        try
        {
            await audit.LogCommandAsync(log);
        }
        catch
        {
            // 见注释
        }
    }

    public static async Task SafeLogSqlAsync(IAuditLogService audit, SqlAuditLog log)
    {
        try
        {
            await audit.LogSqlAsync(log);
        }
        catch
        {
            // 见注释
        }
    }

    /// <summary>
    /// 解析服务器并**禁止歧义**：ID 精确匹配优先；否则按名称/主机名匹配，
    /// 命中多台时返回 <c>server_ambiguous</c> 并列出候选，绝不静默取第一个（否则可能操作错机器）。
    /// </summary>
    public static (SshServerConfig? Server, string? Status, string? Error) ResolveServer(AppConfig config, string reference)
    {
        var byId = config.Servers.Where(s => string.Equals(s.Id, reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byId.Length == 1)
            return AllowIfEnabled(byId[0]);

        var matches = config.Servers.Where(s =>
            (!string.IsNullOrEmpty(s.Name) && string.Equals(s.Name, reference, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(s.Host) && string.Equals(s.Host, reference, StringComparison.OrdinalIgnoreCase))).ToArray();

        if (matches.Length == 1)
            return AllowIfEnabled(matches[0]);

        if (matches.Length > 1)
            return (null, "server_ambiguous", AmbiguousMessage("服务", reference, matches.Select(ServerLabel)));

        var (status, error) = ServerNotFound(config, reference);
        return (null, status, error);
    }

    /// <summary>命中唯一一台后仍要过"禁用"闸门：禁用的服务器一律不放行。</summary>
    private static (SshServerConfig? Server, string? Status, string? Error) AllowIfEnabled(SshServerConfig server) =>
        server.Disabled ? (null, "server_disabled", ServerDisabled(server)) : (server, null, null);

    /// <summary>禁用服务器被调用时的拒绝文案（status=<c>server_disabled</c>）。</summary>
    public static string ServerDisabled(SshServerConfig server) =>
        $"服务器 {ServerLabel(server)} 已被禁用, 已拒绝执行。" +
        "如需使用, 请在桌面 App 的「服务器编辑」里取消勾选\"禁用\"并保存, 或改用其它服务器。";

    /// <summary>服务器展示标签：名称(用户@主机:端口)，供审批确认时核对真实目标。</summary>
    public static string ServerLabel(SshServerConfig server) =>
        $"{server.Name} ({server.Username}@{server.Host}:{server.Port})";

    /// <summary>找不到服务器时的错误文案：必须回显可用 ID，模型才能自我纠正。</summary>
    public static (string Status, string Error) ServerNotFound(AppConfig config, string reference)
    {
        var ids = config.Servers.Select(s => s.Id).ToArray();
        var hint = ids.Length == 0
            ? "配置中没有任何 SSH 服务器, 请先在桌面 App 里添加, 或直接编辑 config.json"
            : $"可用 ID: {string.Join(", ", ids.Take(10))}{(ids.Length > 10 ? " ..." : "")}（也可传服务器名称或主机名）";
        var disabledCount = config.Servers.Count(s => s.Disabled);
        if (disabledCount > 0)
            hint += $"。另有 {disabledCount} 台已禁用的服务器不会出现在 ssh_list_servers 中, 需先在桌面 App 启用";
        return ("server_not_found", $"服务器未找到: {reference}。{hint}");
    }

    public static (string Status, string Error) DatasourceNotFound(AppConfig config, string reference)
    {
        var ids = config.DataSources.Select(d => d.Id).ToArray();
        var hint = ids.Length == 0
            ? "配置中没有任何数据源, 请先在桌面 App 里添加, 或直接编辑 config.json"
            : $"可用 ID: {string.Join(", ", ids.Take(10))}{(ids.Length > 10 ? " ..." : "")}（也可传数据源名称）";
        return ("datasource_not_found", $"数据源未找到: {reference}。{hint}");
    }

    /// <summary>解析数据源并禁止歧义（规则同 <see cref="ResolveServer"/>）。</summary>
    public static (DataSourceConfig? Datasource, string? Status, string? Error) ResolveDatasource(AppConfig config, string reference)
    {
        var byId = config.DataSources.Where(d => string.Equals(d.Id, reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byId.Length == 1)
            return (byId[0], null, null);

        var matches = config.DataSources.Where(d =>
            (!string.IsNullOrEmpty(d.Name) && string.Equals(d.Name, reference, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(d.Host) && string.Equals(d.Host, reference, StringComparison.OrdinalIgnoreCase))).ToArray();

        if (matches.Length == 1)
            return (matches[0], null, null);

        if (matches.Length > 1)
            return (null, "datasource_ambiguous", AmbiguousMessage("数据源", reference, matches.Select(DatasourceLabel)));

        var (status, error) = DatasourceNotFound(config, reference);
        return (null, status, error);
    }

    /// <summary>数据源展示标签：类型 名称(主机:端口)。</summary>
    public static string DatasourceLabel(DataSourceConfig ds) =>
        $"{ds.Type} {ds.Name} ({ds.Host}:{ds.Port})";

    private static string AmbiguousMessage(string kind, string reference, IEnumerable<string> candidates) =>
        $"{kind}标识 '{reference}' 匹配到多个目标, 为避免操作错机器, 已拒绝执行。请在入参中改用精确的 ID: " +
        string.Join("; ", candidates.Select(c => $"[{c}]")) + "。可用列表工具查看确切的 ID。";

    /// <summary>解析应用并禁止歧义（规则同 <see cref="ResolveServer"/>）。</summary>
    public static (ApplicationConfig? Application, string? Status, string? Error) ResolveApplication(AppConfig config, string reference)
    {
        var byId = config.Applications.Where(a => string.Equals(a.Id, reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byId.Length == 1)
            return (byId[0], null, null);

        var matches = config.Applications.Where(a =>
            !string.IsNullOrEmpty(a.Name) && string.Equals(a.Name, reference, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (matches.Length == 1)
            return (matches[0], null, null);

        if (matches.Length > 1)
            return (null, "app_ambiguous", AmbiguousMessage("应用", reference, matches.Select(a => $"{a.Id} / {a.Name}")));

        var (status, error) = ApplicationNotFound(config, reference);
        return (null, status, error);
    }

    public static (string Status, string Error) ApplicationNotFound(AppConfig config, string reference)
    {
        var ids = config.Applications.Select(a => a.Id).ToArray();
        var hint = ids.Length == 0
            ? "配置中没有任何应用, 请先在桌面 App 的「应用管理」里添加, 或直接编辑 config.json"
            : $"可用 ID: {string.Join(", ", ids.Take(10))}{(ids.Length > 10 ? " ..." : "")}（也可传应用名称）";
        return ("app_not_found", $"应用未找到: {reference}。{hint}");
    }

    /// <summary>命令失败时的 status 取值：让模型能区分"可退避重试"与"重试也没用"。</summary>
    public static string CommandFailureStatus(CommandResult? result) =>
        result?.ErrorKind switch
        {
            "rate_limited" => "rate_limited",
            "timeout" => "timeout",
            "auth" => "auth_failed",
            "host_key" => "host_key_mismatch",
            "network" => "connection_error",
            _ => "failed"
        };

    /// <summary>连接探测失败时的 status 取值（与命令失败分类保持一致）。</summary>
    public static string ConnectionFailureStatus(string? errorKind) =>
        errorKind switch
        {
            "auth" => "auth_failed",
            "host_key" => "host_key_mismatch",
            "timeout" => "timeout",
            "network" => "connection_error",
            _ => "connection_error"
        };
}
