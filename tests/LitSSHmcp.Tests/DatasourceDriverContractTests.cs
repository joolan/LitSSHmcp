using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using Xunit;

namespace LitSSHmcp.Tests;

public class DatasourceDriverContractTests
{
    private sealed class FakeDriver : IDatasourceDriver
    {
        public string Type => "fake";

        public Task<DatasourceTestResult> TestAsync(DataSourceConfig ds, CancellationToken ct = default)
            => Task.FromResult(new DatasourceTestResult { Success = true });

        public Task<DatasourceQueryResult> QueryAsync(DataSourceConfig ds, string sql, int maxRows, CancellationToken ct = default)
            => Task.FromResult(new DatasourceQueryResult
            {
                Success = true,
                Columns = new[] { "a" },
                RowCount = 1,
                Rows = { new Dictionary<string, object?> { ["a"] = 1 } }
            });

        public Task<DatasourceExecuteResult> ExecuteAsync(DataSourceConfig ds, string sql, CancellationToken ct = default)
            => Task.FromResult(new DatasourceExecuteResult { Success = true });

        public Task<DatasourceQueryResult> ExplainAsync(DataSourceConfig ds, string sql, CancellationToken ct = default)
            => QueryAsync(ds, sql, 100, ct);

        public Task<DatasourceDiagnosticsResult> DiagnoseAsync(DataSourceConfig ds, CancellationToken ct = default)
            => Task.FromResult(new DatasourceDiagnosticsResult { Success = true });
    }

    [Fact]
    public async Task QueryMultiAsync_defaults_to_single_result()
    {
        IDatasourceDriver driver = new FakeDriver();
        var results = await driver.QueryMultiAsync(new DataSourceConfig(), "select 1", 100);
        Assert.Single(results);
        Assert.True(results[0].Success);
        Assert.Equal(1, results[0].RowCount);
    }

    [Fact]
    public void DbIndexInfo_display_formats_pk_uq_and_plain()
    {
        Assert.Equal("PK PRIMARY (id)", new DbIndexInfo { Name = "PRIMARY", Primary = true, Columns = new[] { "id" } }.Display);
        Assert.Equal("UQ u (a, b)", new DbIndexInfo { Name = "u", Unique = true, Columns = new[] { "a", "b" } }.Display);
        Assert.Equal("i", new DbIndexInfo { Name = "i" }.Display);
    }

    [Fact]
    public void DbObjectInfo_display_includes_schema_when_present()
    {
        Assert.Equal("public.users", new DbObjectInfo { Schema = "public", Name = "users" }.Display);
        Assert.Equal("users", new DbObjectInfo { Name = "users" }.Display);
    }
}
