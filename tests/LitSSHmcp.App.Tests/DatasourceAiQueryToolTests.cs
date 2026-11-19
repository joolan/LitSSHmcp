using System.Text.Json;
using LitSSHmcp.App.Services.DatasourceAi;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class DatasourceAiQueryToolTests
{
    [Fact]
    public async Task Insert_decision_does_not_execute_and_is_audited()
    {
        var driver = new FakeDriver();
        var audit = new FakeAudit();
        string? inserted = null;
        var tool = CreateTool(driver, audit, DatasourceAiSqlDecision.Insert,
            sql => inserted = sql, (_, _) => { });

        var result = await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["sql"] = "SELECT * FROM orders"
        }, CancellationToken.None);

        Assert.Contains("\"inserted\":true", result?.ToString());
        Assert.Null(driver.LastSql);
        Assert.Equal("SELECT * FROM orders", inserted);
        var log = Assert.Single(audit.SqlLogs);
        Assert.Equal("dsai:9", log.SessionId);
        Assert.Equal("workbench_ai", log.Tool);
        Assert.Equal("inserted-to-editor", log.Decision);
    }

    [Fact]
    public async Task Execute_decision_returns_rows_and_shows_result()
    {
        var driver = new FakeDriver();
        var audit = new FakeAudit();
        DatasourceQueryResult? shown = null;
        var tool = CreateTool(driver, audit, DatasourceAiSqlDecision.Execute,
            _ => { }, (_, result) => shown = result);

        var result = await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["sql"] = "SELECT id FROM orders",
            ["maxRows"] = 10
        }, CancellationToken.None);

        Assert.Contains("\"executed\":true", result?.ToString());
        Assert.Equal("SELECT id FROM orders", driver.LastSql);
        Assert.Equal(10, driver.LastMaxRows);
        Assert.NotNull(shown);
        Assert.Single(shown!.Rows);
        var log = Assert.Single(audit.SqlLogs);
        Assert.Equal("manual-approved", log.Decision);
        Assert.Equal(CommandStatus.Executed, log.Status);
    }

    [Fact]
    public async Task Cancel_decision_does_not_execute()
    {
        var driver = new FakeDriver();
        var audit = new FakeAudit();
        var tool = CreateTool(driver, audit, DatasourceAiSqlDecision.Cancel, _ => { }, (_, _) => { });

        var result = await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["sql"] = "SELECT 1"
        }, CancellationToken.None);

        Assert.Contains("\"rejected\":true", result?.ToString());
        Assert.Null(driver.LastSql);
        Assert.Equal("manual-rejected", Assert.Single(audit.SqlLogs).Decision);
    }

    [Fact]
    public async Task Sensitive_read_sql_is_blocked_before_confirmation()
    {
        var driver = new FakeDriver();
        var audit = new FakeAudit();
        var confirmed = false;
        var tool = new DatasourceAiQueryTool(
            Scope(), new FakeRegistry(driver), new SensitiveFilter(), audit, null,
            (_, _, _) => { confirmed = true; return Task.FromResult(DatasourceAiSqlDecision.Execute); },
            _ => { }, (_, _) => { });

        var result = await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["sql"] = "SELECT * FROM orders FOR UPDATE"
        }, CancellationToken.None);

        Assert.False(confirmed);
        Assert.Null(driver.LastSql);
        using var doc = JsonDocument.Parse(result!.ToString()!);
        Assert.Contains("敏感", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("sensitive-blocked", Assert.Single(audit.SqlLogs).Decision);
    }

    [Fact]
    public async Task Write_sql_is_blocked_before_confirmation()
    {
        var driver = new FakeDriver();
        var audit = new FakeAudit();
        var confirmed = false;
        var tool = new DatasourceAiQueryTool(
            Scope(), new FakeRegistry(driver), new AllowFilter(), audit, null,
            (_, _, _) => { confirmed = true; return Task.FromResult(DatasourceAiSqlDecision.Execute); },
            _ => { }, (_, _) => { });

        var result = await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["sql"] = "DELETE FROM orders"
        }, CancellationToken.None);

        Assert.False(confirmed);
        Assert.Null(driver.LastSql);
        using var doc = JsonDocument.Parse(result!.ToString()!);
        Assert.Contains("不是只读查询", doc.RootElement.GetProperty("error").GetString());
    }

    private static DatasourceAiQueryTool CreateTool(
        FakeDriver driver, FakeAudit audit, DatasourceAiSqlDecision decision,
        DatasourceAiSqlSink insert, DatasourceAiResultSink show)
        => new(Scope(), new FakeRegistry(driver), new AllowFilter(), audit, null,
            (_, _, _) => Task.FromResult(decision), insert, show);

    private static DatasourceAiScope Scope()
        => new(new DataSourceConfig
        {
            Id = "ds1",
            Name = "Shop",
            Type = "mysql"
        }, "shop", "dsai:9");

    private sealed class FakeRegistry : IDatasourceDriverRegistry
    {
        private readonly IDatasourceDriver _driver;
        public FakeRegistry(IDatasourceDriver driver) => _driver = driver;
        public IDatasourceDriver? Get(string type) => _driver;
        public IDatasourceDriver GetRequired(string type) => _driver;
        public IReadOnlyCollection<string> SupportedTypes => new[] { "mysql" };
    }

    private sealed class AllowFilter : ISqlFilterService
    {
        public SqlFilterResult CheckReadOnly(string sql)
            => sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                ? SqlFilterResult.Blocked
                : SqlFilterResult.Allowed;

        public SqlFilterResult CheckWrite(string sql) => SqlFilterResult.Allowed;
    }

    private sealed class SensitiveFilter : ISqlFilterService
    {
        public SqlFilterResult CheckReadOnly(string sql) => SqlFilterResult.Sensitive;
        public SqlFilterResult CheckWrite(string sql) => SqlFilterResult.Blocked;
    }

    private sealed class FakeDriver : IDatasourceDriver
    {
        public string? LastSql { get; private set; }
        public int LastMaxRows { get; private set; }
        public string Type => "mysql";

        public Task<DatasourceTestResult> TestAsync(DataSourceConfig ds, CancellationToken ct = default)
            => Task.FromResult(new DatasourceTestResult { Success = true });

        public Task<DatasourceQueryResult> QueryAsync(
            DataSourceConfig ds, string sql, int maxRows, CancellationToken ct = default)
        {
            LastSql = sql;
            LastMaxRows = maxRows;
            return Task.FromResult(new DatasourceQueryResult
            {
                Success = true,
                Columns = new[] { "id" },
                RowCount = 1,
                Rows = new List<Dictionary<string, object?>> { new() { ["id"] = 1 } }
            });
        }

        public Task<DatasourceExecuteResult> ExecuteAsync(
            DataSourceConfig ds, string sql, CancellationToken ct = default)
            => Task.FromResult(new DatasourceExecuteResult { Success = true });

        public Task<DatasourceQueryResult> ExplainAsync(
            DataSourceConfig ds, string sql, CancellationToken ct = default)
            => QueryAsync(ds, sql, 100, ct);

        public Task<DatasourceDiagnosticsResult> DiagnoseAsync(
            DataSourceConfig ds, CancellationToken ct = default)
            => Task.FromResult(new DatasourceDiagnosticsResult { Success = true });
    }

    private sealed class FakeAudit : IAuditLogService
    {
        public List<SqlAuditLog> SqlLogs { get; } = new();
        public string? SessionId { get; set; }

        public Task InitializeAsync() => Task.CompletedTask;
        public Task LogCommandAsync(CommandAuditLog log) => Task.CompletedTask;
        public Task LogSqlAsync(SqlAuditLog log)
        {
            SqlLogs.Add(log);
            return Task.CompletedTask;
        }

        public Task<CommandAuditLog[]> GetLogsAsync(
            string? serverId = null, int limit = 100, string? keyword = null,
            bool includeHistory = false, int offset = 0, string? sessionId = null,
            string? tool = null, AuditCategory? category = null,
            IReadOnlyCollection<string>? userIds = null, string? toolPrefix = null)
            => Task.FromResult(Array.Empty<CommandAuditLog>());

        public Task<SqlAuditLog[]> GetSqlLogsAsync(
            string? dataSourceId = null, int limit = 100, string? keyword = null,
            bool includeHistory = false, int offset = 0, string? sessionId = null,
            string? tool = null, AuditCategory? category = null,
            IReadOnlyCollection<string>? userIds = null, string? toolPrefix = null)
            => Task.FromResult(SqlLogs.ToArray());

        public Task RecordSessionAsync(AuditSession session) => Task.CompletedTask;
        public Task<AuditSession[]> GetSessionsAsync(int limit = 50, string? ownerUserId = null)
            => Task.FromResult(Array.Empty<AuditSession>());
        public Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default)
            => Task.FromResult(new AuditVerifyResult { Ok = true });
    }
}
