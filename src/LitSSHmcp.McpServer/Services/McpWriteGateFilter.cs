using System.Text.Json;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 目标级写互斥请求过滤器（评估报告 §4.3 / M2-1）：写类工具按目标跨进程串行，读类自由并行。
/// <list type="bullet">
/// <item>写类工具 = 常写工具（上传/重启/docker exec…） + 条件写工具（执行命令/SQL/Redis 按参数判定只读）；</item>
/// <item>目标键经 ToolSupport 归一为 ssh:{Id} / ds:{Id}（与 TargetLimiter 同构，ID/名称/主机名引用均可解析）；</item>
/// <item>拿不到写锁（超时）直接返回 write_busy 错误 —— 同一目标写操作互斥，稍后重试；</item>
/// <item>无法归一化目标（未知/歧义/缺配置）时放行 —— 此时工具自身必会报错，不会产生真实写竞态。</item>
/// </list>
/// </summary>
public static class McpWriteGateFilter
{
    /// <summary>
    /// 等待目标写锁的上限；超时返回 write_busy（与审批耗时解耦，客户端可稍后重试）。
    /// 默认 5 秒与错误文案里的重试建议一致；短写（上传/重启）可排队，长写尽快让路。
    /// </summary>
    public static TimeSpan WriteWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>常写工具：工具语义即改写目标（对照 54 工具清单过出的写类，读类工具如 ssh_download_file 不在内）。</summary>
    private static readonly HashSet<string> AlwaysWriteTools = new(StringComparer.Ordinal)
    {
        "ssh_upload_file", "ssh_upload_files",
        "service_restart", "docker_restart", "docker_exec",
        "ssh_execute_sudo" // sudo 提权命令不可预测，一律按写类串行（宁严勿松）
    };

    /// <summary>条件写工具：默认按写类，参数确为只读时放行不占写锁。</summary>
    private static readonly HashSet<string> ConditionalWriteTools = new(StringComparer.Ordinal)
    {
        "ssh_execute_command", "mysql_execute", "postgres_execute", "redis_execute"
    };

    /// <summary>
    /// Shell 命令只读白名单（首词）。方向宁严勿松：未命中 = 写类（占写锁），
    /// 只是把纯读命令误判成写类 —— 最多多串行一次，绝不把写命令误放行为读类。
    /// </summary>
    private static readonly HashSet<string> ReadOnlyFirstTokens = new(StringComparer.Ordinal)
    {
        "ls", "cat", "head", "tail", "less", "more", "grep", "egrep", "fgrep", "rg",
        "wc", "stat", "file", "du", "df", "free", "uptime", "whoami", "id", "hostname",
        "uname", "printenv", "pwd", "which", "type", "groups", "getent", "ps", "top",
        "pidof", "readlink", "realpath", "md5sum", "sha1sum", "sha256sum", "cksum",
        "cut", "sort", "uniq", "tr", "nl", "rev", "column", "jq", "echo", "printf",
        "journalctl", "last", "lastlog", "w", "who", "dmesg", "lsblk", "lscpu", "lsmem",
        "lsusb", "lspci", "lsmod", "lsof", "ss", "netstat", "arp", "ping", "traceroute",
        "tracepath", "nslookup", "dig", "host", "diff", "comm", "join", "paste", "od",
        "hexdump", "xxd", "strings", "man"
    };

    /// <summary>docker 只读子命令（docker exec/stop/rm/run 等一律写类）。</summary>
    private static readonly HashSet<string> DockerReadOnlySubcommands = new(StringComparer.Ordinal)
    {
        "ps", "logs", "inspect", "stats", "images", "version", "info", "top", "df"
    };

    /// <summary>systemctl 只读子命令（start/stop/restart/enable/edit 等一律写类）。</summary>
    private static readonly HashSet<string> SystemctlReadOnlySubcommands = new(StringComparer.Ordinal)
    {
        "status", "is-active", "is-enabled", "is-failed", "show", "list-units", "list-unit-files", "cat"
    };

    /// <summary>find 的写类参数（删除/执行/写文件），命中即判写；其余 find 视为纯遍历。</summary>
    private static readonly string[] FindWriteMarkers = { "-delete", "-exec", "-execdir", "-ok", "-fprint", "-fls", "-fprintf" };

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateFilter()
    {
        McpRequestFilter<CallToolRequestParams, CallToolResult> filter = next => async (context, ct) =>
        {
            var tool = context.Params?.Name;
            if (string.IsNullOrEmpty(tool) || !RequiresWriteGate(tool))
                return await next(context, ct);

            var services = context.Services;
            var args = context.Params?.Arguments;

            var sqlFilter = services?.GetService(typeof(ISqlFilterService)) as ISqlFilterService;
            if (IsReadCall(tool, args, sqlFilter))
                return await next(context, ct);

            var mutex = services?.GetService(typeof(ITargetWriteMutex)) as ITargetWriteMutex;
            if (mutex is null)
                return await next(context, ct);

            var targetKey = await ResolveTargetKeyAsync(services, tool, args, ct).ConfigureAwait(false);
            if (targetKey is null)
                return await next(context, ct); // 归一化失败：工具自身会拒绝，无真实写竞态

            var (lease, reason) = await mutex.AcquireAsync(targetKey, WriteWait, ct).ConfigureAwait(false);
            if (lease is null)
                return WriteBusy(targetKey, reason);

            try
            {
                return await next(context, ct).ConfigureAwait(false);
            }
            finally
            {
                lease.Dispose();
            }
        };

        return filter;
    }

    /// <summary>该工具调用是否需要过写锁（常写或条件写工具）。</summary>
    public static bool RequiresWriteGate(string? toolName) =>
        !string.IsNullOrEmpty(toolName) &&
        (AlwaysWriteTools.Contains(toolName!) || ConditionalWriteTools.Contains(toolName!));

    /// <summary>
    /// 条件写工具的只读判定：SQL 走 ISqlFilterService（Allowed/Sensitive=只读），
    /// Redis 走 RedisCommandPolicy（ReadOnly），Shell 走首词白名单 + 控制符拆段。
    /// </summary>
    public static bool IsReadCall(string toolName, IDictionary<string, JsonElement>? args, ISqlFilterService? sqlFilter)
    {
        var command = GetStringArg(args, "command");
        var sql = GetStringArg(args, "sql");

        return toolName switch
        {
            "ssh_execute_command" => LooksReadOnlyShellCommand(command),
            "mysql_execute" or "postgres_execute" =>
                sql is not null && sqlFilter is not null && IsReadOnlySql(sqlFilter, sql),
            "redis_execute" =>
                command is not null && RedisCommandPolicy.Classify(command) == RedisCommandKind.ReadOnly,
            _ => false // 常写工具与未知工具一律按写类
        };
    }

    /// <summary>
    /// Shell 命令只读判定（宁严勿松）：无重定向/子命令替换，且按 &amp;&amp; || ; | &amp; 拆出的每一段
    /// 首词都在只读白名单内（或 docker/systemctl/find 的只读子形态）。
    /// </summary>
    public static bool LooksReadOnlyShellCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        if (command.Contains('`') || command.Contains("$("))
            return false; // 子命令替换内容不可预测

        if (!RedirectsAreNullDevNull(command))
            return false;

        var segments = command.Split(new[] { "&&", "||", ";", "|", "&" }, StringSplitOptions.None);
        foreach (var raw in segments)
        {
            var segment = raw.Trim();
            if (segment.Length == 0 || segment.StartsWith('('))
                return false;

            if (!IsReadOnlySegment(segment))
                return false;
        }

        return true;
    }

    private static bool IsReadOnlySegment(string segment)
    {
        var parts = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var head = parts[0].ToLowerInvariant();

        // 白名单词的写形态 / 按子命令细分的特殊词（须先于首词白名单判定）
        if (head is "date" or "env")
            return parts.Length == 1;

        if (head == "docker")
            return parts.Length >= 2 &&
                   DockerReadOnlySubcommands.Contains(parts[1].TrimStart('-').ToLowerInvariant());

        if (head == "systemctl")
            return parts.Length >= 2 &&
                   SystemctlReadOnlySubcommands.Contains(parts[1].ToLowerInvariant());

        if (head == "journalctl")
            return !HasVacuumFlag(segment);

        if (head == "sysctl")
            return !segment.Contains('=') && !parts.Skip(1).Any(p => p is "-w" or "--write");

        if (head == "find")
            return !FindWriteMarkers.Any(marker => segment.Contains(marker, StringComparison.Ordinal));

        return ReadOnlyFirstTokens.Contains(head);
    }

    private static bool HasVacuumFlag(string segment) =>
        segment.Contains("--vacuum", StringComparison.Ordinal);

    /// <summary>
    /// 重定向只允许丢到 /dev/null（<c>df -h 2&gt;/dev/null</c> 这类常见读命令仍算只读）；
    /// 任何写文件的重定向、或无法解析的 <c>&gt;</c>（如引号内）都判写。
    /// </summary>
    private static bool RedirectsAreNullDevNull(string command)
    {
        if (!command.Contains('>') && !command.Contains('<'))
            return true;

        var matches = Regex.Matches(command, @"\d*(?:>>?)\s*([^\s;|&]+)");
        if (matches.Count == 0)
            return false; // 有 >/< 但解析不出目标（多在引号里）→ 保守判写

        foreach (Match match in matches)
        {
            var target = match.Groups[1].Value.TrimEnd('"', '\'');
            if (!string.Equals(target, "/dev/null", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool IsReadOnlySql(ISqlFilterService sqlFilter, string sql)
    {
        var result = sqlFilter.CheckReadOnly(sql);
        return result is SqlFilterResult.Allowed or SqlFilterResult.Sensitive;
    }

    /// <summary>把工具参数里的 ID/名称/主机名引用归一为与 TargetLimiter 同构的目标键。</summary>
    public static async Task<string?> ResolveTargetKeyAsync(
        IServiceProvider? services, string toolName, IDictionary<string, JsonElement>? args, CancellationToken ct = default)
    {
        if (services is null)
            return null;

        var configService = services.GetService(typeof(IConfigService)) as IConfigService;
        if (configService is null)
            return null;

        AppConfig config;
        try
        {
            config = await configService.LoadConfigAsync().ConfigureAwait(false);
        }
        catch
        {
            return null; // 配置读不到 → 工具自身也无法定位目标 → 放行
        }

        return ResolveTargetKey(config, toolName, args);
    }

    /// <summary>
    /// 同 <see cref="ResolveTargetKeyAsync"/>，但直接使用调用方已加载的配置（避免同一次过滤里重复读盘）；
    /// 授权矩阵过滤器（McpAuthorizationFilter）与写互斥共用同一归一化逻辑。
    /// </summary>
    public static string? ResolveTargetKey(AppConfig config, string toolName, IDictionary<string, JsonElement>? args)
    {
        if (config is null)
            return null;

        if (toolName.StartsWith("ssh_", StringComparison.Ordinal) ||
            toolName is "service_restart" or "docker_restart" or "docker_exec")
        {
            var reference = GetStringArg(args, "serverId");
            if (string.IsNullOrWhiteSpace(reference))
                return null;
            var (server, _, _) = ToolSupport.ResolveServer(config, reference);
            return server is null ? null : $"ssh:{server.Id}";
        }

        var datasourceReference = GetStringArg(args, "datasourceId");
        if (string.IsNullOrWhiteSpace(datasourceReference))
            return null;
        var (datasource, _, _) = ToolSupport.ResolveDatasource(config, datasourceReference);
        return datasource is null ? null : $"ds:{datasource.Id}";
    }

    private static string? GetStringArg(IDictionary<string, JsonElement>? args, string name)
    {
        if (args is null || !args.TryGetValue(name, out var element))
            return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static CallToolResult WriteBusy(string targetKey, string? reason) => new()
    {
        IsError = true,
        Content = new List<ContentBlock>
        {
            new TextContentBlock
            {
                Text = $"目标 {targetKey} 正有其它写操作在执行, 同一目标的写操作必须串行" +
                       $"({reason ?? "write_busy"}), 请稍后重试(建议等待5秒)。"
            }
        }
    };
}
