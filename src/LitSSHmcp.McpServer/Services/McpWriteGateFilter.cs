using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 目标级写互斥请求过滤器（评估报告 §4.3 / M2-1）：写类工具按目标跨进程串行，读类自由并行。
/// <list type="bullet">
/// <item>是否需要写锁由 <see cref="ToolSecurityCatalog"/> 的 <c>RequiresWriteLock</c> 决定，不再维护工具名集合；</item>
/// <item>条件写工具（SQL/Redis）只有在语句确为只读时才跳过写锁；</item>
/// <item>shell 命令一律按写类占锁，不再使用“首词只读”判定来降低并发保护；</item>
/// <item>目标键经 ToolSupport 归一为 <c>ssh:{Id}</c> / <c>ds:{Id}</c>（ID/名称/主机名引用均可解析）；</item>
/// <item>拿不到写锁（超时）直接返回 <c>write_busy</c> 错误，同一目标写操作互斥，稍后重试；</item>
/// <item>无法归一化目标时放行，交由工具自身拒绝。</item>
/// </list>
/// </summary>
public static class McpWriteGateFilter
{
    /// <summary>
    /// 等待目标写锁的上限；超时返回 write_busy（与审批耗时解耦，客户端可稍后重试）。
    /// 默认 5 秒与错误文案里的重试建议一致；短写（上传/重启）可排队，长写尽快让路。
    /// </summary>
    public static TimeSpan WriteWait { get; set; } = TimeSpan.FromSeconds(5);

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateFilter()
    {
        McpRequestFilter<CallToolRequestParams, CallToolResult> filter = next => async (context, ct) =>
        {
            var tool = context.Params?.Name;
            var descriptor = ToolSecurityCatalog.Find(tool);
            if (string.IsNullOrEmpty(tool) || descriptor is null || !descriptor.RequiresWriteLock)
                return await next(context, ct);

            var services = context.Services;
            var args = context.Params?.Arguments;

            var sqlFilter = services?.GetService(typeof(ISqlFilterService)) as ISqlFilterService;
            if (descriptor.IsConditionalWrite && IsReadCall(tool, args, sqlFilter))
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

    /// <summary>该工具调用是否需要过写锁（由安全目录的 RequiresWriteLock 决定）。</summary>
    public static bool RequiresWriteGate(string? toolName) =>
        ToolSecurityCatalog.Find(toolName)?.RequiresWriteLock == true;

    /// <summary>
    /// 条件写工具的只读判定：SQL 仅 <see cref="SqlFilterResult.Allowed"/> 算只读，
    /// Redis 仅 <see cref="RedisCommandKind.ReadOnly"/> 算只读；其他工具一律按写类。
    /// </summary>
    public static bool IsReadCall(string toolName, IDictionary<string, JsonElement>? args, ISqlFilterService? sqlFilter)
    {
        var command = GetStringArg(args, "command");
        var sql = GetStringArg(args, "sql");

        return toolName switch
        {
            "mysql_execute" or "postgres_execute" =>
                sql is not null && sqlFilter is not null && IsReadOnlySql(sqlFilter, sql),
            "redis_execute" =>
                command is not null && RedisCommandPolicy.Classify(command) == RedisCommandKind.ReadOnly,
            _ => false
        };
    }

    private static bool IsReadOnlySql(ISqlFilterService sqlFilter, string sql) =>
        sqlFilter.CheckReadOnly(sql) == SqlFilterResult.Allowed;

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
            return null; // 配置读不到时工具自身也无法定位目标，放行
        }

        return ResolveTargetKey(config, toolName, args);
    }

    /// <summary>
    /// 同 <see cref="ResolveTargetKeyAsync"/>，但直接使用调用方已加载的配置（避免同一次过滤里重复读盘）。
    /// </summary>
    public static string? ResolveTargetKey(AppConfig config, string toolName, IDictionary<string, JsonElement>? args)
    {
        if (config is null)
            return null;

        // 批量变更使用全局写锁，避免多个批量任务同时改写多台机器。
        if (string.Equals(toolName, "ssh_execute_fleet", StringComparison.Ordinal))
            return "fleet:all";

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
                       $"({reason ?? "write_busy"}), 请稍后重试 (建议等待 5 秒)。"
            }
        }
    };
}
