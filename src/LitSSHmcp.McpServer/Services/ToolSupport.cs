using System.Text.RegularExpressions;
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

    /// <summary>把该服务器所有非空口令/密钥替换为 ******（返回给模型前的最终兜底，确保任何路径/异常都不外泄密码）。</summary>
    public static string RedactSecrets(string? text, SshServerConfig server)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var result = text;
        foreach (var secret in new[] { server.Password, server.KeyFilePassphrase, server.SudoPassword })
            if (!string.IsNullOrEmpty(secret))
                result = result.Replace(secret, "******");
        return result;
    }

    /// <summary>把审批结果 + 当前审批模式映射为审计里的“决策说明”（Gate 类）。</summary>
    public static string? DecisionFor(ApprovalOutcome outcome, string? mode)
    {
        var m = (mode ?? string.Empty).Trim().ToLowerInvariant();
        var autoApprove = m is "auto-approve" or "auto_approve" or "autoapprove" or "allow" or "approve";
        var autoReject = m is "auto-reject" or "auto_reject" or "autoreject" or "deny" or "reject";
        return outcome switch
        {
            ApprovalOutcome.Approved => autoApprove ? "auto-approve" : "manual-approved",
            ApprovalOutcome.Rejected => autoReject ? "auto-reject" : "manual-rejected",
            ApprovalOutcome.AutoRejected => "auto-reject",
            ApprovalOutcome.Timeout => "timeout",
            ApprovalOutcome.Unavailable => "unavailable",
            _ => null
        };
    }

    /// <summary>SQL/Redis 审计事件类型：Gate=写审批/拦截；Probe=测试/诊断/EXPLAIN；其余=Exec。</summary>
    public static AuditCategory SqlCategory(SqlOperation operation, CommandStatus status) =>
        status is CommandStatus.Blocked or CommandStatus.Approved or CommandStatus.Rejected
            ? AuditCategory.Gate
            : operation is SqlOperation.Explain or SqlOperation.Diagnostics or SqlOperation.Test
                ? AuditCategory.Probe
                : AuditCategory.Exec;

    /// <summary>SQL/Redis 审计决策说明（Gate 类）。</summary>
    public static string? SqlDecision(CommandStatus status, string? note) => status switch
    {
        CommandStatus.Blocked => "blocked",
        CommandStatus.Approved => (note?.Contains("自动") == true ||
                                   (note?.Contains("AutoApprove", StringComparison.OrdinalIgnoreCase) ?? false))
            ? "auto-approve" : "manual-approved",
        CommandStatus.Rejected => "manual-rejected",
        _ => null
    };

    /// <summary>
    /// 识别“必然挂起 / 需要交互”的命令，返回替代建议；无风险返回 null。
    /// 这些命令在无 TTY 的 exec 通道里会一直等待或持续输出，导致连接不返回、拿不到结果；
    /// 与其等 60 秒超时，不如直接给模型明确的替代写法。仅对用户直传的命令生效（工具内部构造的命令不经过此检查）。
    /// </summary>
    public static string? BlockingCommandHint(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        // 跟随日志（-f/--follow）：会持续输出、不返回
        if (Regex.IsMatch(command, @"(?i)\btail\b[^\r\n|;&]*(\s-f\b|\s-F\b|--follow)"))
            return "检测到 `tail -f/-F/--follow`：会一直跟随文件、连接不返回。请改用一次性查看 `tail -n 200 <file>`，或直接用 log_tail/log_grep 工具。";
        if (Regex.IsMatch(command, @"(?i)\bdocker\s+(compose\s+)?logs\b[^\r\n|;&]*(--follow|\s-f\b)"))
            return "检测到 `docker logs -f/--follow`：会持续跟随。请用 `docker logs --tail 200 <container>`，或直接用 docker_logs 工具。";
        if (Regex.IsMatch(command, @"(?i)\bjournalctl\b[^\r\n|;&]*(--follow|\s-f\b)"))
            return "检测到 `journalctl -f/--follow`。请用 `journalctl -n 200 --no-pager`，或直接用 service_logs 工具。";
        if (Regex.IsMatch(command, @"(?i)\bkubectl\s+logs\b[^\r\n|;&]*(--follow|\s-f\b)"))
            return "检测到 `kubectl logs -f/--follow`。请用 `kubectl logs --tail=200 <pod> -n <ns>`。";

        // 交互式分页/编辑器/监视器：等待按键、不返回
        if (Regex.IsMatch(command, @"(?i)(^|[;&|]\s*|\bsudo\s+|\bnohup\s+)(vi|vim|nano|emacs|less|more|top|htop|watch)\b"))
            return "检测到交互式命令(编辑器/分页/监视器)：无 TTY 会挂起。请改用非交互方式，如 `ps`/`ss`/`free`/`df`，日志用 `head`/`tail -n`/`--no-pager`。";

        // 交互式 shell / 需要 stdin
        if (Regex.IsMatch(command, @"(?i)(^|[;&|]\s*)(read|bash\s+-i|sh\s+-i|python\s+-i|bc|sqlplus|mysql)\b"))
            return "检测到需要交互输入的命令：MCP 无 TTY、无法输入。请改为非交互方式(参数化/管道输入)。";

        // 普通通道里的 sudo/su：无 TTY 会等密码
        if (Regex.IsMatch(command, @"(?i)(^|[;&|]\s*|\bnohup\s+)sudo\b"))
            return "普通命令通道里的 `sudo` 会因无 TTY 等待密码而挂起。请改用 ssh_execute_sudo(方式由服务器配置的 SudoType 决定)。";
        if (Regex.IsMatch(command, @"(?i)(^|[;&|]\s*|\bnohup\s+)su\b"))
            return "`su` 需要 TTY/密码，会挂起。请改用 ssh_execute_sudo。";

        // ping 未限定次数 / nc/telnet 交互连接
        if (Regex.IsMatch(command, @"(?i)(^|[;&|]\s*)ping\b") && !Regex.IsMatch(command, @"(?i)\s-[a-z]*c\s*\d"))
            return "`ping` 未限定次数会一直运行。请用 `ping -c 4 <host>`。";
        if (Regex.IsMatch(command, @"(?i)(^|[;&|]\s*)(nc|netcat|telnet)\b"))
            return "`nc/telnet` 交互连接会挂起。探活请用 `timeout 5 bash -c '</dev/tcp/<host>/<port>' && echo open || echo closed`。";

        // docker 交互
        if (Regex.IsMatch(command, @"(?i)\bdocker\s+exec\b[^\r\n|;&]*\s(?:-it|-ti|-i\s+-t|-t\s+-i)\b"))
            return "检测到 `docker exec -it`：交互式会挂起。请去掉 `-it`，用 `docker exec <container> <cmd>`。";
        if (Regex.IsMatch(command, @"(?i)(^|[;&|]\s*)docker\s+attach\b"))
            return "`docker attach` 会附着到容器前台并可能阻塞。请改用 `docker logs --tail 200 <container>`。";

        return null;
    }

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
