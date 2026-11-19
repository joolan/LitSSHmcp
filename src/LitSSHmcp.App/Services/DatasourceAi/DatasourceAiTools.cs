using System.Text;
using System.Text.Json;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Sql;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.Services.DatasourceAi;

public enum DatasourceAiSqlDecision
{
    Insert,
    Execute,
    Cancel
}

public delegate Task<DatasourceAiSqlDecision> DatasourceAiSqlConfirm(
    string sql, string summary, CancellationToken ct);

public delegate void DatasourceAiSqlSink(string sql);
public delegate void DatasourceAiResultSink(string sql, DatasourceQueryResult result);

public sealed class DatasourceAiSchemaTool : IAgentTool
{
    public const string ToolName = "datasource_schema";
    private readonly DatasourceAiScope _scope;
    private readonly IDatasourceDriverRegistry _registry;
    private readonly Func<DbTableInfo?> _selectedTable;
    private readonly AITool _schema;

    public DatasourceAiSchemaTool(
        DatasourceAiScope scope, IDatasourceDriverRegistry registry, Func<DbTableInfo?> selectedTable)
    {
        _scope = scope;
        _registry = registry;
        _selectedTable = selectedTable;
        _schema = AIFunctionFactory.Create(
            (string? table = null) => (table),
            Name,
            Description);
    }

    public string Name => ToolName;
    public bool Destructive => false;
    public bool ReadOnly => true;
    public string? ToolGroup => "datasource";
    public bool IsLocal => true;
    public AITool Tool => _schema;

    private string Description =>
        $"读取当前数据源 {_scope.Datasource.Name} / 库 {_scope.Database} 的表结构。" +
        "不传 table 时返回当前选中表完整字段 + 其他表名列表；传 table 时返回指定表完整字段。" +
        "只能访问当前数据源，不能访问其他数据源。";

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        try
        {
            var driver = _registry.GetRequired(_scope.Kind);
            if (driver is not IDatabaseExplorer explorer)
                return Error("当前数据源不支持结构浏览。");

            var requested = arguments is not null && arguments.TryGetValue("table", out var t)
                ? t?.ToString()?.Trim()
                : null;

            if (string.IsNullOrWhiteSpace(requested))
                return await new DatasourceAiContextBuilder(_registry)
                    .BuildSchemaDigestAsync(_scope, _selectedTable(), ct);

            var tables = await explorer.ListTablesAsync(_scope.Effective, _scope.Database, ct);
            var table = tables.FirstOrDefault(x =>
                            string.Equals(x.Display, requested, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.Name, requested, StringComparison.OrdinalIgnoreCase));
            if (table is null)
                return Error($"当前库 {_scope.Database} 中未找到表 {requested}。");

            var columns = await explorer.ListColumnsAsync(
                _scope.Effective, _scope.Database, table.Name, table.Schema, ct);
            var sb = new StringBuilder();
            sb.AppendLine($"表：{table.Display}");
            if (!string.IsNullOrWhiteSpace(table.Comment))
                sb.AppendLine($"注释：{table.Comment}");
            if (table.Rows is long rows)
                sb.AppendLine($"估算行数：{rows:N0}");
            sb.AppendLine("字段：");
            foreach (var column in columns)
            {
                var flags = new List<string>();
                if (column.PrimaryKey) flags.Add("PK");
                if (!column.Nullable) flags.Add("NOT NULL");
                if (column.Default is not null) flags.Add("DEFAULT " + column.Default);
                var suffix = flags.Count > 0 ? " [" + string.Join(", ", flags) + "]" : string.Empty;
                var comment = string.IsNullOrWhiteSpace(column.Comment) ? string.Empty : " // " + column.Comment;
                sb.AppendLine($"- {column.Name} {column.Type}{suffix}{comment}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private static string Error(string message)
        => JsonSerializer.Serialize(new { ok = false, error = message });
}

public sealed class DatasourceAiExplainTool : IAgentTool
{
    public const string ToolName = "datasource_explain";
    private readonly DatasourceAiScope _scope;
    private readonly IDatasourceDriverRegistry _registry;
    private readonly ISqlFilterService _filter;
    private readonly IAuditLogService _audit;
    private readonly MaskingConfig? _masking;
    private readonly AITool _schema;

    public DatasourceAiExplainTool(
        DatasourceAiScope scope,
        IDatasourceDriverRegistry registry,
        ISqlFilterService filter,
        IAuditLogService audit,
        MaskingConfig? masking)
    {
        _scope = scope;
        _registry = registry;
        _filter = filter;
        _audit = audit;
        _masking = masking;
        _schema = AIFunctionFactory.Create(
            (string sql) => sql,
            Name,
            Description);
    }

    public string Name => ToolName;
    public bool Destructive => false;
    public bool ReadOnly => true;
    public string? ToolGroup => "datasource";
    public bool IsLocal => true;
    public AITool Tool => _schema;

    private string Description =>
        "对当前数据源中的一条只读 SQL 生成执行计划（EXPLAIN），不会执行数据查询。" +
        "只允许 SELECT/WITH 等只读语句；用于分析索引、扫描方式和慢查询原因。";

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var sql = GetString(arguments, "sql")?.Trim();
        if (string.IsNullOrWhiteSpace(sql))
            return Error("缺少 sql 参数。");
        if (sql.Contains('\0'))
            return Error("SQL 包含非法字符。");
        var statements = SqlScriptSplitter.Split(sql);
        if (statements.Count != 1)
            return Error("datasource_explain 只接受单条 SQL。");
        if (IsDatabaseSwitch(sql))
            return Error("数据源 AI 不允许切换数据库。");
        var filterResult = _filter.CheckReadOnly(sql);
        if (filterResult != SqlFilterResult.Allowed)
            return Error(filterResult == SqlFilterResult.Sensitive
                ? "该 SQL 包含敏感/写入关键字（例如 EXPLAIN ANALYZE DML），已拒绝 EXPLAIN。"
                : "该 SQL 不是只读查询，已拒绝 EXPLAIN。");

        try
        {
            var result = await _registry.GetRequired(_scope.Kind)
                .ExplainAsync(_scope.Effective, sql, ct);
            await AuditAsync(sql, result.Success, result.DurationMs, result.Error);
            return JsonSerializer.Serialize(new
            {
                ok = result.Success,
                executed = true,
                summary = DatasourceAiContextBuilder.BuildResultDigest(result, _masking, 50),
                error = result.Error
            });
        }
        catch (Exception ex)
        {
            await AuditAsync(sql, false, 0, ex.Message);
            return Error(ex.Message);
        }
    }

    private async Task AuditAsync(string sql, bool success, double durationMs, string? error)
    {
        try
        {
            await _audit.LogSqlAsync(new SqlAuditLog
            {
                DataSourceId = _scope.DatasourceId,
                DataSourceName = _scope.Datasource.Name,
                Operation = SqlOperation.Explain,
                Sql = sql,
                Status = success ? CommandStatus.Executed : CommandStatus.Failed,
                Result = success ? "ok" : error,
                DurationMs = durationMs,
                SessionId = _scope.SessionKey,
                Tool = "workbench_ai",
                Category = AuditCategory.Probe,
                Decision = "read-only"
            });
        }
        catch
        {
            // 审计失败不阻塞 AI 的只读分析
        }
    }

    private static string? GetString(IDictionary<string, object?>? args, string key)
        => args is not null && args.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool IsDatabaseSwitch(string sql)
        => SqlFilterConfig.GetFirstKeyword(sql).Equals("use", StringComparison.OrdinalIgnoreCase);

    private static string Error(string message)
        => JsonSerializer.Serialize(new { ok = false, error = message });
}

public sealed class DatasourceAiQueryTool : IAgentTool
{
    public const string ToolName = "datasource_query";
    private readonly DatasourceAiScope _scope;
    private readonly IDatasourceDriverRegistry _registry;
    private readonly ISqlFilterService _filter;
    private readonly IAuditLogService _audit;
    private readonly MaskingConfig? _masking;
    private readonly DatasourceAiSqlConfirm _confirm;
    private readonly DatasourceAiSqlSink _insertSql;
    private readonly DatasourceAiResultSink _showResult;
    private readonly AITool _schema;

    public DatasourceAiQueryTool(
        DatasourceAiScope scope,
        IDatasourceDriverRegistry registry,
        ISqlFilterService filter,
        IAuditLogService audit,
        MaskingConfig? masking,
        DatasourceAiSqlConfirm confirm,
        DatasourceAiSqlSink insertSql,
        DatasourceAiResultSink showResult)
    {
        _scope = scope;
        _registry = registry;
        _filter = filter;
        _audit = audit;
        _masking = masking;
        _confirm = confirm;
        _insertSql = insertSql;
        _showResult = showResult;
        _schema = AIFunctionFactory.Create(
            (string sql, int maxRows = 100) => (sql, maxRows),
            Name,
            Description);
    }

    public string Name => ToolName;
    public bool Destructive => false;
    public bool ReadOnly => true;
    public string? ToolGroup => "datasource";
    public bool IsLocal => true;
    public AITool Tool => _schema;

    private string Description =>
        "在聊天中生成一条只读 SQL，并交给用户确认。" +
        "用户可以选择插入编辑器（不执行）或直接执行；每次调用都会弹窗确认。" +
        "只允许单条 SELECT/WITH/SHOW/EXPLAIN/DESC 查询；执行结果会以摘要 + 前 20 行自动回传到当前会话。" +
        "返回中的 rowCount 是本次取回行数，truncated=true 时真实总数未知，不能把 20 行当作总数。" +
        "不能执行写操作，不能访问其他数据源。";

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var sql = GetString(arguments, "sql")?.Trim();
        if (string.IsNullOrWhiteSpace(sql))
            return Error("缺少 sql 参数。");
        if (sql.Contains('\0'))
            return Error("SQL 包含非法字符。");

        var statements = SqlScriptSplitter.Split(sql);
        if (statements.Count != 1)
            return Error("datasource_query 只接受单条 SQL。请把多条语句拆开。");
        if (IsDatabaseSwitch(sql))
            return Error("数据源 AI 不允许切换数据库。");
        var filterResult = _filter.CheckReadOnly(sql);
        if (filterResult != SqlFilterResult.Allowed)
        {
            var sensitive = filterResult == SqlFilterResult.Sensitive;
            await AuditAsync(sql, CommandStatus.Blocked,
                sensitive ? "sensitive-blocked" : "blocked",
                sensitive ? "只读模式拒绝含敏感/写入关键字的 SQL" : "只读模式拒绝非查询 SQL",
                null, 0, AuditCategory.Gate);
            return Error(sensitive
                ? "该 SQL 包含敏感/写入关键字（例如 CTE/EXPLAIN 中的 DML），已拒绝执行。"
                : "该 SQL 不是只读查询，已拒绝执行。");
        }

        var maxRows = arguments is not null && arguments.TryGetValue("maxRows", out var maxValue) &&
                      int.TryParse(maxValue?.ToString(), out var parsed)
            ? Math.Clamp(parsed, 1, 500)
            : 100;

        DatasourceAiSqlDecision decision;
        try
        {
            decision = await _confirm(sql, $"数据源 {_scope.Datasource.Name} · 库 {_scope.Database}", ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error("确认 SQL 失败：" + ex.Message);
        }

        if (decision == DatasourceAiSqlDecision.Cancel)
        {
            await AuditAsync(sql, CommandStatus.Rejected, "manual-rejected", "用户取消", null, 0,
                AuditCategory.Gate);
            return JsonSerializer.Serialize(new
            {
                ok = false,
                executed = false,
                rejected = true,
                message = "用户取消了该 SQL。不要重复提交相同 SQL，除非用户明确要求。"
            });
        }

        if (decision == DatasourceAiSqlDecision.Insert)
        {
            _insertSql(sql);
            await AuditAsync(sql, CommandStatus.Rejected, "inserted-to-editor", "已插入编辑器，未执行", null, 0,
                AuditCategory.Gate);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                executed = false,
                inserted = true,
                message = "SQL 已插入工作台编辑器，等待用户手动执行。不要声称已经执行或看到结果。"
            });
        }

        try
        {
            var result = await _registry.GetRequired(_scope.Kind)
                .QueryAsync(_scope.Effective, sql, maxRows, ct);
            ResultMasker.Apply(result.Columns, result.Rows, _masking);
            _showResult(sql, result);
            await AuditAsync(sql,
                result.Success ? CommandStatus.Executed : CommandStatus.Failed,
                "manual-approved",
                result.Success ? "ok" : result.Error,
                result.RowCount,
                result.DurationMs);

            var summary = DatasourceAiContextBuilder.BuildResultDigest(result, _masking);
            return JsonSerializer.Serialize(new
            {
                ok = result.Success,
                executed = true,
                rowCount = result.RowCount,
                returnedRows = result.Rows.Count,
                truncated = result.Truncated,
                totalKnown = !result.Truncated,
                injectedRows = Math.Min(20, result.Rows.Count),
                durationMs = result.DurationMs,
                columns = result.Columns,
                summary,
                note = result.Truncated
                    ? "本次只取回上限行数并被截断，真实总数未知；需要总数请执行 COUNT(*) 或聚合查询。"
                    : "本次查询已返回全部行；AI 摘要仍最多只包含前 20 行。",
                error = result.Error
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await AuditAsync(sql, CommandStatus.Failed, "manual-approved", ex.Message, null, 0);
            return Error(ex.Message);
        }
    }

    private async Task AuditAsync(
        string sql, CommandStatus status, string decision, string? result, long? rows, double durationMs,
        AuditCategory category = AuditCategory.Exec)
    {
        try
        {
            await _audit.LogSqlAsync(new SqlAuditLog
            {
                DataSourceId = _scope.DatasourceId,
                DataSourceName = _scope.Datasource.Name,
                Operation = SqlOperation.Query,
                Sql = sql,
                Status = status,
                Result = result,
                RowsAffected = rows,
                DurationMs = durationMs,
                SessionId = _scope.SessionKey,
                Tool = "workbench_ai",
                Category = category,
                Decision = decision
            });
        }
        catch
        {
            // 审计失败不阻塞此轮对话，但调用方仍能看到结果。
        }
    }

    private static string? GetString(IDictionary<string, object?>? args, string key)
        => args is not null && args.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool IsDatabaseSwitch(string sql)
        => SqlFilterConfig.GetFirstKeyword(sql).Equals("use", StringComparison.OrdinalIgnoreCase);

    private static string Error(string message)
        => JsonSerializer.Serialize(new { ok = false, error = message });
}
