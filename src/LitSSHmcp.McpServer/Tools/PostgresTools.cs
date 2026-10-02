// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class PostgresTools
{
    private readonly IConfigService _configService;
    private readonly IDatasourceDriverRegistry _driverRegistry;
    private readonly ISqlFilterService _sqlFilter;
    private readonly IApprovalService _approvalService;
    private readonly IAuditLogService _auditLogService;
    private readonly ISecurityOptionsProvider _securityOptions;

    public PostgresTools(
        IConfigService configService,
        IDatasourceDriverRegistry driverRegistry,
        ISqlFilterService sqlFilter,
        IApprovalService approvalService,
        IAuditLogService auditLogService,
        ISecurityOptionsProvider securityOptions)
    {
        _configService = configService;
        _driverRegistry = driverRegistry;
        _sqlFilter = sqlFilter;
        _approvalService = approvalService;
        _auditLogService = auditLogService;
        _securityOptions = securityOptions;
    }

    [McpServerTool(Name = "postgres_query", UseStructuredContent = true, OutputSchemaType = typeof(QueryResultDto), ReadOnly = true, OpenWorld = true)]
    [Description("在PostgreSQL数据源执行只读SQL(SELECT/SHOW/EXPLAIN/WITH)。写SQL用postgres_execute, 整体诊断用postgres_diagnostics")]
    public async Task<QueryResultDto> PostgresQuery(
        [Description("数据源标识: ID/名称, 可用datasource_list列出")] string datasourceId,
        [Description("只读SQL语句")] string sql,
        [Description("最大返回行数(默认100, 上限1000)")] int maxRows = 100,
        CancellationToken cancellationToken = default)
    {
        var (ds, driver, config, status, error) = await ResolveAsync(datasourceId);
        if (ds == null || driver == null) return QueryResultDto.Fail(status!, error!);

        maxRows = Math.Clamp(maxRows, 1, 1000);
        if (ds.MaxRows is int maxRowsCap && maxRowsCap > 0)
            maxRows = Math.Min(maxRows, maxRowsCap);

        var filterResult = _sqlFilter.CheckReadOnly(sql);
        if (filterResult == SqlFilterResult.Blocked)
        {
            await AuditAsync(ds, SqlOperation.Query, sql, CommandStatus.Blocked,
                "非只读或多语句或命中禁止规则, 只读查询仅允许 SELECT/SHOW/EXPLAIN/WITH");
            return QueryResultDto.Fail("blocked",
                "该语句不允许在只读查询工具中执行。postgres_query 仅允许 SELECT/SHOW/EXPLAIN/WITH。" +
                "写操作请使用 postgres_execute(会经过安全过滤与用户审批)。");
        }

        if (filterResult == SqlFilterResult.Sensitive)
        {
            await AuditAsync(ds, SqlOperation.Query, sql, CommandStatus.Blocked,
                "只读通道检测到写语句(数据修改CTE或EXPLAIN ANALYZE)");
            return QueryResultDto.Fail("not_readonly_statement",
                "该语句以只读关键字(WITH/EXPLAIN)开头但包含写操作(数据修改 CTE 或 EXPLAIN ANALYZE <DML>), " +
                "不能走只读查询。请改用 postgres_execute, 它会做安全过滤并需要用户审批。");
        }

        using var cts = CreateTimeout(ds, cancellationToken);
        var result = await driver.QueryAsync(ds, sql, maxRows, cts.Token);

        if (result.Success)
            ResultMasker.Apply(result.Columns, result.Rows, config.Security.Masking);

        await AuditAsync(ds, SqlOperation.Query, sql,
            result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            result.Success ? $"rows={result.RowCount} truncated={result.Truncated}" : Truncate(result.Error, 500),
            result.RowCount, result.DurationMs);

        return new QueryResultDto
        {
            Success = result.Success,
            Status = result.Success ? null : "query_error",
            Error = result.Error,
            DatasourceId = ds.Id,
            Name = ds.Name,
            Columns = result.Columns,
            Rows = result.Rows,
            RowCount = result.RowCount,
            Truncated = result.Truncated,
            DurationMs = result.DurationMs
        };
    }

    [McpServerTool(Name = "postgres_execute", UseStructuredContent = true, OutputSchemaType = typeof(ExecuteResultDto), Destructive = true, OpenWorld = true)]
    [Description("在PostgreSQL数据源执行写SQL(INSERT/UPDATE/DELETE/DDL)。危险语句拒绝、敏感语句桌面确认; 只读用postgres_query")]
    public async Task<ExecuteResultDto> PostgresExecute(
        [Description("数据源标识: ID/名称, 可用datasource_list列出")] string datasourceId,
        [Description("要执行的SQL语句(单条)")] string sql,
        CancellationToken cancellationToken = default)
    {
        var (ds, driver, _, status, error) = await ResolveAsync(datasourceId);
        if (ds == null || driver == null) return ExecuteResultDto.Fail(status!, error!);

        if (ds.ReadOnly)
        {
            await AuditAsync(ds, SqlOperation.Execute, sql, CommandStatus.Blocked, "数据源为只读, 拒绝写操作");
            return ExecuteResultDto.Fail("readonly_datasource", $"数据源 {ds.Name} 已配置为只读, 拒绝写操作。");
        }

        var keyword = SqlFilterConfig.GetFirstKeyword(sql);
        if (IsReadOnlyRoute(sql, keyword))
            return ExecuteResultDto.Fail("readonly_statement", "这是只读语句, 请改用 postgres_query 执行。");

        var filterResult = _sqlFilter.CheckWrite(sql);
        if (filterResult == SqlFilterResult.Blocked)
        {
            await AuditAsync(ds, SqlOperation.Execute, sql, CommandStatus.Blocked, "命中SQL禁止规则");
            return ExecuteResultDto.Fail("blocked",
                "该SQL被安全策略禁止执行(如无WHERE的DELETE/UPDATE、DROP TABLE/DATABASE、GRANT等)。" +
                "被禁止的语句不会以任何方式执行。");
        }

        if (filterResult == SqlFilterResult.Sensitive)
        {
            if (ds.WriteApproval == WriteApprovalMode.AutoApprove)
            {
                await AuditAsync(ds, SqlOperation.Execute, sql, CommandStatus.Approved, "数据源策略自动放行(WriteApproval=AutoApprove)");
            }
            else
            {
                var outcome = await _approvalService.RequestApprovalAsync(
                    ToolSupport.DatasourceLabel(ds), sql, CommandFilterResult.Sensitive, null, "SQL写操作", cancellationToken);

                if (outcome != ApprovalOutcome.Approved)
                {
                    var (failStatus, failError) = ApprovalOutcomeText.Describe(outcome, _securityOptions.Approval.TimeoutSeconds);
                    await AuditAsync(ds, SqlOperation.Execute, sql, CommandStatus.Rejected, failStatus);
                    return ExecuteResultDto.Fail(failStatus, failError);
                }

                await AuditAsync(ds, SqlOperation.Execute, sql, CommandStatus.Approved, "用户批准");
            }
        }

        using var cts = CreateTimeout(ds, cancellationToken);
        var result = await driver.ExecuteAsync(ds, sql, cts.Token);

        await AuditAsync(ds, SqlOperation.Execute, sql,
            result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            result.Success ? $"rowsAffected={result.RowsAffected}" : Truncate(result.Error, 500),
            result.RowsAffected, result.DurationMs);

        return new ExecuteResultDto
        {
            Success = result.Success,
            Status = result.Success ? null : "execute_error",
            Error = result.Error,
            DatasourceId = ds.Id,
            Name = ds.Name,
            RowsAffected = result.RowsAffected,
            DurationMs = result.DurationMs
        };
    }

    [McpServerTool(Name = "postgres_explain", UseStructuredContent = true, OutputSchemaType = typeof(QueryResultDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("对SQL执行EXPLAIN分析执行计划(PostgreSQL)")]
    public async Task<QueryResultDto> PostgresExplain(
        [Description("数据源标识: ID/名称, 可用datasource_list列出")] string datasourceId,
        [Description("要分析的SELECT语句")] string sql,
        CancellationToken cancellationToken = default)
    {
        var (ds, driver, _, status, error) = await ResolveAsync(datasourceId);
        if (ds == null || driver == null) return QueryResultDto.Fail(status!, error!);

        var filterResult = _sqlFilter.CheckReadOnly(sql);
        if (filterResult == SqlFilterResult.Blocked)
            return QueryResultDto.Fail("blocked", "EXPLAIN 仅接受只读SELECT/WITH语句。");
        if (filterResult == SqlFilterResult.Sensitive)
            return QueryResultDto.Fail("not_readonly_statement",
                "该语句含写操作(数据修改 CTE 或 EXPLAIN ANALYZE <DML>), 不能在 postgres_explain 中执行, 请改用 postgres_execute。");

        using var cts = CreateTimeout(ds, cancellationToken);
        var result = await driver.ExplainAsync(ds, sql, cts.Token);

        await AuditAsync(ds, SqlOperation.Explain, $"EXPLAIN {Truncate(sql, 400)}",
            result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            result.Success ? $"rows={result.RowCount}" : Truncate(result.Error, 500),
            result.RowCount, result.DurationMs);

        return new QueryResultDto
        {
            Success = result.Success,
            Status = result.Success ? null : "explain_error",
            Error = result.Error,
            DatasourceId = ds.Id,
            Name = ds.Name,
            Columns = result.Columns,
            Rows = result.Rows,
            RowCount = result.RowCount,
            DurationMs = result.DurationMs
        };
    }

    [McpServerTool(Name = "postgres_diagnostics", UseStructuredContent = true, OutputSchemaType = typeof(DiagnosticsResultDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("PostgreSQL整体诊断: 连接/活动会话/等待锁/复制/缓存命中/死锁。问'PG慢/卡/连接数暴涨'时优先用; 查具体数据用postgres_query")]
    public async Task<DiagnosticsResultDto> PostgresDiagnostics(
        [Description("数据源标识: ID/名称, 可用datasource_list列出")] string datasourceId,
        CancellationToken cancellationToken = default)
    {
        var (ds, driver, _, status, error) = await ResolveAsync(datasourceId);
        if (ds == null || driver == null) return DiagnosticsResultDto.Fail(status!, error!);

        using var cts = CreateTimeout(ds, cancellationToken);
        var result = await driver.DiagnoseAsync(ds, cts.Token);

        await AuditAsync(ds, SqlOperation.Diagnostics, "-- diagnostics",
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

    private async Task<(DataSourceConfig? Ds, IDatasourceDriver? Driver, AppConfig Config, string? Status, string? Error)> ResolveAsync(string datasourceId)
    {
        var config = await _configService.LoadConfigAsync();
        var (ds, notFoundStatus, notFoundError) = ToolSupport.ResolveDatasource(config, datasourceId);
        if (ds == null)
            return (null, null, config, notFoundStatus, notFoundError);

        if (!ds.Type.Equals("postgres", StringComparison.OrdinalIgnoreCase))
            return (null, null, config, "unsupported_type",
                $"数据源 {ds.Name} 的类型是 {ds.Type}, 不是 postgres。PostgreSQL 工具只能在 type=postgres 的数据源上执行。");

        var driver = _driverRegistry.Get(ds.Type);
        if (driver == null)
            return (null, null, config, "unsupported_type", $"暂不支持的数据源类型: {ds.Type}");

        return (ds, driver, config, null, null);
    }

    private static CancellationTokenSource CreateTimeout(DataSourceConfig ds, CancellationToken ct = default)
    {
        var cts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        cts.CancelAfter(ds.TimeoutSeconds is int s && s > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(30));
        return cts;
    }

    /// <summary>
    /// 首关键字只读不等于整条语句只读: WITH 后面可以接数据修改 CTE, EXPLAIN 后面可以接
    /// ANALYZE &lt;DML&gt;(MySQL/PG 都会真的执行)。只有这些"看起来只读"的语句确实只读时, 才路由到 *_query。
    /// </summary>
    private bool IsReadOnlyRoute(string sql, string keyword) =>
        keyword is "select" or "show" or "desc" or "describe"
        || (keyword is "with" or "explain" && !_securityOptions.SqlFilter.IsSensitive(sql));

    private async Task AuditAsync(
        DataSourceConfig ds,
        SqlOperation operation,
        string sql,
        CommandStatus status,
        string? result,
        long? rows = null,
        double? durationMs = null)
    {
        var audit = _securityOptions.Audit;
        var storedSql = audit.StoreSqlText
            ? (audit.MaskLiterals ? SqlRedactor.Mask(sql) : sql)
            : "[未记录: storeSqlText=false]";

        await _auditLogService.LogSqlAsync(new SqlAuditLog
        {
            DataSourceId = ds.Id,
            DataSourceName = ds.Name,
            Operation = operation,
            Sql = storedSql.Length > 4000 ? storedSql[..4000] + "..." : storedSql,
            Status = status,
            Result = result,
            RowsAffected = rows,
            DurationMs = durationMs
        });
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max] + "...");
}
