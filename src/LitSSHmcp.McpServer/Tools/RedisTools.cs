// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

/// <summary>
/// Redis 工具组（第一期:只读诊断/只读命令 + 写命令审批）。
/// 安全模型与 MySQL 一致: 密码只在本机, AI 只用 datasourceId 引用; 命令经
/// <see cref="RedisCommandPolicy"/> 分类(只读白名单/危险拒绝/写操作审批)并全部写入审计。
/// </summary>
[McpServerToolType]
public class RedisTools
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly IConfigService _configService;
    private readonly IRedisConnectionProvider _connectionProvider;
    private readonly IApprovalService _approvalService;
    private readonly IAuditLogService _auditLogService;
    private readonly ISecurityOptionsProvider _securityOptions;

    public RedisTools(
        IConfigService configService,
        IRedisConnectionProvider connectionProvider,
        IApprovalService approvalService,
        IAuditLogService auditLogService,
        ISecurityOptionsProvider securityOptions)
    {
        _configService = configService;
        _connectionProvider = connectionProvider;
        _approvalService = approvalService;
        _auditLogService = auditLogService;
        _securityOptions = securityOptions;
    }

    [McpServerTool(Name = "redis_diagnostics", UseStructuredContent = true, OutputSchemaType = typeof(DiagnosticsResultDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Redis整体诊断: 内存/客户端/命中率/键空间/慢日志/主从/持久化。问'缓存慢/内存涨/命中率低'时优先用; 看具体key用redis_read")]
    public async Task<DiagnosticsResultDto> RedisDiagnostics(
        [Description("数据源ID, 可用datasource_list列出(type=redis)")] string datasourceId)
    {
        var (ds, status, error) = await ResolveAsync(datasourceId);
        if (ds == null) return DiagnosticsResultDto.Fail(status!, error!);

        try
        {
            var driver = new RedisDriver(_connectionProvider);
            var result = await driver.DiagnoseAsync(ds);

            await AuditAsync(ds, SqlOperation.Diagnostics, "-- redis diagnostics",
                result.Success ? CommandStatus.Executed : CommandStatus.Failed,
                result.Success ? Truncate(result.Summary, 500) : Truncate(result.Error, 500),
                null, result.DurationMs);

            return new DiagnosticsResultDto
            {
                Success = result.Success,
                Status = result.Success ? null : "diagnostics_error",
                Error = result.Error,
                DatasourceId = ds.Id,
                Name = ds.Name,
                Summary = result.Summary,
                Data = result.Data,
                DurationMs = result.DurationMs
            };
        }
        catch (Exception ex)
        {
            await AuditAsync(ds, SqlOperation.Diagnostics, "-- redis diagnostics", CommandStatus.Failed, ex.Message);
            return DiagnosticsResultDto.Fail("connection_error", ex.Message);
        }
    }

    [McpServerTool(Name = "redis_read", UseStructuredContent = true, OutputSchemaType = typeof(RedisCommandResultDto), ReadOnly = true, OpenWorld = true)]
    [Description("在Redis数据源执行只读命令(GET/HGETALL/INFO/SLOWLOG等白名单)。写命令用redis_execute, 整体体检用redis_diagnostics; 含空格的值用引号, 如 HGETALL user:1")]
    public async Task<RedisCommandResultDto> RedisRead(
        [Description("数据源ID, 可用datasource_list列出")] string datasourceId,
        [Description("Redis命令, 如 GET key / HGETALL user:1 / SLOWLOG GET 10")] string command,
        [Description("数组返回的最大元素数(默认200, 上限1000), 超出截断")] int maxItems = 200)
    {
        var (ds, status, error) = await ResolveAsync(datasourceId);
        if (ds == null) return RedisCommandResultDto.Fail(status!, error!);

        var args = Parse(command, out var parseError);
        if (args == null)
            return RedisCommandResultDto.Fail("invalid_command", parseError!);

        var kind = RedisCommandPolicy.Classify(command);
        if (kind == RedisCommandKind.Blocked)
        {
            await AuditAsync(ds, SqlOperation.Query, command, CommandStatus.Blocked, "命中Redis禁止规则");
            return RedisCommandResultDto.Fail("blocked",
                $"命令 {RedisCommandPolicy.CommandName(command)} 被安全策略禁止执行" +
                "（清库/关服/换主从/加载模块/阻塞连接类命令）。被禁止的命令不会以任何方式执行。");
        }

        if (kind == RedisCommandKind.Write)
        {
            await AuditAsync(ds, SqlOperation.Query, command, CommandStatus.Blocked, "非只读命令");
            return RedisCommandResultDto.Fail("not_readonly",
                $"命令 {RedisCommandPolicy.CommandName(command)} 不在只读白名单内。" +
                "只读查询请用 redis_read 中的读命令; 写/管理操作请使用 redis_execute（会经过用户桌面审批）。");
        }

        maxItems = Math.Clamp(maxItems, 1, 1000);

        var (session, openError) = await OpenSessionAsync(ds);
        if (session == null)
        {
            await AuditAsync(ds, SqlOperation.Query, command, CommandStatus.Failed, openError);
            return RedisCommandResultDto.Fail("connection_error", openError!);
        }

        try
        {
            using var _ = session;
            using var timeout = CreateTimeout(ds);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var reply = await session.Client.ExecuteAsync(args, timeout.Token);
            stopwatch.Stop();

            if (reply.IsError)
            {
                await AuditAsync(ds, SqlOperation.Query, command, CommandStatus.Failed,
                    Truncate(reply.ErrorMessage, 500), null, stopwatch.Elapsed.TotalMilliseconds);
                return RedisCommandResultDto.Fail("redis_error", reply.ErrorMessage ?? "Redis 返回错误");
            }

            var formatted = RedisValueFormatter.Format(reply, maxItems);

            await AuditAsync(ds, SqlOperation.Query, command, CommandStatus.Executed,
                reply.Kind == RedisValueKind.Integer ? $"value={reply.Integer}" : "ok",
                reply.Kind == RedisValueKind.Integer ? reply.Integer : null,
                stopwatch.Elapsed.TotalMilliseconds);

            return new RedisCommandResultDto
            {
                Success = true,
                DatasourceId = ds.Id,
                Name = ds.Name,
                Command = args,
                Result = JsonSerializer.Serialize(formatted.Value),
                Truncated = formatted.Truncated,
                DurationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                AccessMode = session.AccessMode,
                ViaTunnelServer = session.ViaTunnelServer
            };
        }
        catch (Exception ex)
        {
            await AuditAsync(ds, SqlOperation.Query, command, CommandStatus.Failed, Truncate(ex.Message, 500));
            return RedisCommandResultDto.Fail("connection_error", ex.Message);
        }
    }

    [McpServerTool(Name = "redis_execute", UseStructuredContent = true, OutputSchemaType = typeof(RedisCommandResultDto), Destructive = true, OpenWorld = true)]
    [Description("在Redis数据源执行写/管理命令(SET/DEL/EXPIRE/CONFIG SET等)。危险命令拒绝、其余一律桌面确认; 只读用redis_read")]
    public async Task<RedisCommandResultDto> RedisExecute(
        [Description("数据源ID, 可用datasource_list列出")] string datasourceId,
        [Description("要执行的Redis写命令, 含空格的值用引号包裹, 如 SET session:1 'abc' EX 60")] string command)
    {
        var (ds, status, error) = await ResolveAsync(datasourceId);
        if (ds == null) return RedisCommandResultDto.Fail(status!, error!);

        var args = Parse(command, out var parseError);
        if (args == null)
            return RedisCommandResultDto.Fail("invalid_command", parseError!);

        var kind = RedisCommandPolicy.Classify(command);
        switch (kind)
        {
            case RedisCommandKind.Blocked:
                await AuditAsync(ds, SqlOperation.Execute, command, CommandStatus.Blocked, "命中Redis禁止规则");
                return RedisCommandResultDto.Fail("blocked",
                    $"命令 {RedisCommandPolicy.CommandName(command)} 被安全策略禁止执行" +
                    "（清库/关服/换主从/加载模块/阻塞连接类命令）。被禁止的命令不会以任何方式执行。");

            case RedisCommandKind.ReadOnly:
                return RedisCommandResultDto.Fail("readonly_statement", "这是只读命令, 请改用 redis_read 执行。");
        }

        if (ds.ReadOnly)
        {
            await AuditAsync(ds, SqlOperation.Execute, command, CommandStatus.Blocked, "数据源为只读, 拒绝写操作");
            return RedisCommandResultDto.Fail("readonly_datasource", $"数据源 {ds.Name} 已配置为只读, 拒绝写操作。");
        }

        if (ds.WriteApproval == WriteApprovalMode.AutoApprove)
        {
            await AuditAsync(ds, SqlOperation.Execute, command, CommandStatus.Approved, "数据源策略自动放行(WriteApproval=AutoApprove)");
        }
        else
        {
            // 默认策略: 所有 Redis 写操作一律需要用户桌面确认
            var approved = await _approvalService.RequestApprovalAsync(
                $"数据源 {ds.Name}", command, CommandFilterResult.Sensitive, null, "Redis写操作");

            if (!approved)
            {
                await AuditAsync(ds, SqlOperation.Execute, command, CommandStatus.Rejected, "用户拒绝");
                return RedisCommandResultDto.Fail("rejected", "Redis写操作被用户拒绝执行。");
            }

            await AuditAsync(ds, SqlOperation.Execute, command, CommandStatus.Approved, "用户批准");
        }

        var (session, openError) = await OpenSessionAsync(ds);
        if (session == null)
        {
            await AuditAsync(ds, SqlOperation.Execute, command, CommandStatus.Failed, openError);
            return RedisCommandResultDto.Fail("connection_error", openError!);
        }

        try
        {
            using var _ = session;
            using var timeout = CreateTimeout(ds);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var reply = await session.Client.ExecuteAsync(args, timeout.Token);
            stopwatch.Stop();

            if (reply.IsError)
            {
                await AuditAsync(ds, SqlOperation.Execute, command, CommandStatus.Failed,
                    Truncate(reply.ErrorMessage, 500), null, stopwatch.Elapsed.TotalMilliseconds);
                return RedisCommandResultDto.Fail("redis_error", reply.ErrorMessage ?? "Redis 返回错误");
            }

            await AuditAsync(ds, SqlOperation.Execute, command, CommandStatus.Executed,
                reply.Kind == RedisValueKind.Integer ? $"value={reply.Integer}" : "ok",
                reply.Kind == RedisValueKind.Integer ? reply.Integer : null,
                stopwatch.Elapsed.TotalMilliseconds);

            return new RedisCommandResultDto
            {
                Success = true,
                DatasourceId = ds.Id,
                Name = ds.Name,
                Command = args,
                Result = JsonSerializer.Serialize(RedisValueFormatter.Format(reply, 100).Value),
                DurationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2)
            };
        }
        catch (Exception ex)
        {
            await AuditAsync(ds, SqlOperation.Execute, command, CommandStatus.Failed, Truncate(ex.Message, 500));
            return RedisCommandResultDto.Fail("connection_error", ex.Message);
        }
    }

    private async Task<(DataSourceConfig? Ds, string? Status, string? Error)> ResolveAsync(string datasourceId)
    {
        var config = await _configService.LoadConfigAsync();
        var ds = config.DataSources.FirstOrDefault(d => d.Id == datasourceId);
        if (ds == null)
            return (null, "datasource_not_found", $"数据源未找到: {datasourceId}。可用ID见 datasource_list。");

        if (!ds.Type.Equals("redis", StringComparison.OrdinalIgnoreCase))
            return (null, "unsupported_type",
                $"数据源 {ds.Name} 的类型是 {ds.Type}, 不是 redis。Redis 命令只能在 type=redis 的数据源上执行。");

        return (ds, null, null);
    }

    private async Task<(IRedisSession? Session, string? Error)> OpenSessionAsync(DataSourceConfig ds)
    {
        try
        {
            return (await _connectionProvider.OpenAsync(ds), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static string[]? Parse(string? command, out string? error)
    {
        try
        {
            var args = RedisCommandParser.Split(command);
            error = null;
            return args;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static CancellationTokenSource CreateTimeout(DataSourceConfig ds) =>
        ds.TimeoutSeconds is int s && s > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(s))
            : new CancellationTokenSource(CommandTimeout);

    private async Task AuditAsync(
        DataSourceConfig ds,
        SqlOperation operation,
        string command,
        CommandStatus status,
        string? result,
        long? rows = null,
        double? durationMs = null)
    {
        var audit = _securityOptions.Audit;
        var stored = audit.StoreSqlText
            ? (audit.MaskLiterals ? SqlRedactor.Mask(command) : command)
            : "[未记录: storeSqlText=false]";

        await _auditLogService.LogSqlAsync(new SqlAuditLog
        {
            DataSourceId = ds.Id,
            DataSourceName = ds.Name,
            Operation = operation,
            Sql = stored.Length > 4000 ? stored[..4000] + "..." : stored,
            Status = status,
            Result = result,
            RowsAffected = rows,
            DurationMs = durationMs
        });
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max] + "...");
}
