using System.Diagnostics;
using System.Globalization;
using LitSSHmcp.Core.Models;
using Npgsql;

namespace LitSSHmcp.Core.Services.Datasource;

/// <summary>
/// PostgreSQL 数据源驱动：连通性测试、只读查询、写执行、执行计划与运行诊断。
/// 复用 <see cref="IDatasourceDriver"/> 抽象，因此 mcp_self_check / datasource_test_connection /
/// 界面"测试连接" / 拓扑发现都能直接支持 postgres 类型数据源。
/// </summary>
public class PostgresDriver : IDatasourceDriver
{
    private readonly IPostgresConnectionProvider _connectionProvider;

    public string Type => "postgres";

    public PostgresDriver(IPostgresConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public async Task<DatasourceTestResult> TestAsync(DataSourceConfig ds, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var session = await _connectionProvider.OpenAsync(ds, ct);
            stopwatch.Stop();

            string? version = null;
            await using (var cmd = session.Connection.CreateCommand())
            {
                cmd.CommandText = "SELECT version()";
                version = (await cmd.ExecuteScalarAsync(ct)) as string;
            }

            return new DatasourceTestResult
            {
                Success = true,
                Message = "连接成功",
                Version = version,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                AccessMode = session.AccessMode,
                ViaTunnelServer = session.ViaTunnelServer
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DatasourceTestResult
            {
                Success = false,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                AccessMode = ds.AccessMode.ToString(),
                Error = ex.Message
            };
        }
    }

    public async Task<DatasourceQueryResult> QueryAsync(DataSourceConfig ds, string sql, int maxRows, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var session = await _connectionProvider.OpenAsync(ds, ct);
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = sql;

            var result = new DatasourceQueryResult();
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            result.Columns = columns;

            while (await reader.ReadAsync(ct))
            {
                if (result.Rows.Count >= maxRows)
                {
                    result.Truncated = true;
                    break;
                }
                result.Rows.Add(ReadRow(reader, columns));
            }

            stopwatch.Stop();
            result.Success = true;
            result.RowCount = result.Rows.Count;
            result.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DatasourceQueryResult
            {
                Success = false,
                Error = ex.Message,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
    }

    public async Task<DatasourceExecuteResult> ExecuteAsync(DataSourceConfig ds, string sql, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var session = await _connectionProvider.OpenAsync(ds, ct);
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = sql;

            var affected = await cmd.ExecuteNonQueryAsync(ct);

            stopwatch.Stop();
            return new DatasourceExecuteResult
            {
                Success = true,
                RowsAffected = affected,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DatasourceExecuteResult
            {
                Success = false,
                Error = ex.Message,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
    }

    public async Task<DatasourceQueryResult> ExplainAsync(DataSourceConfig ds, string sql, CancellationToken ct = default)
    {
        var normalized = sql.Trim().TrimEnd(';');
        var keyword = SqlFilterConfig.GetFirstKeyword(normalized);
        return keyword is "explain"
            ? await QueryAsync(ds, normalized, 100, ct)
            : await QueryAsync(ds, $"EXPLAIN {normalized}", 100, ct);
    }

    public async Task<DatasourceDiagnosticsResult> DiagnoseAsync(DataSourceConfig ds, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var session = await _connectionProvider.OpenAsync(ds, ct);
            var connection = session.Connection;
            var data = new Dictionary<string, object?>();

            var version = await ScalarStringAsync(connection, "SELECT version()", ct);
            var database = await ScalarStringAsync(connection, "SELECT current_database()", ct);
            var maxConnections = await ScalarLongAsync(connection, "SELECT current_setting('max_connections')::int", ct);

            data["version"] = version;
            data["database"] = database;
            data["serverVersionNum"] = await ScalarStringAsync(connection, "SHOW server_version", ct);

            var totalConnections = await ScalarLongAsync(connection, "SELECT count(*) FROM pg_stat_activity", ct);
            var activeConnections = await ScalarLongAsync(connection, "SELECT count(*) FROM pg_stat_activity WHERE state = 'active'", ct);
            data["connections"] = new Dictionary<string, object?>
            {
                ["current"] = totalConnections,
                ["active"] = activeConnections,
                ["max"] = maxConnections
            };

            data["activityByState"] = await GroupCountAsync(connection, "SELECT COALESCE(state,'(unknown)'), count(*) FROM pg_stat_activity GROUP BY 1", ct);
            data["longestQueries"] = await QueryMapAsync(connection,
                "SELECT pid, usename, datname, state, " +
                "EXTRACT(EPOCH FROM (now() - query_start))::int AS elapsed_sec, " +
                "left(query, 500) AS query " +
                "FROM pg_stat_activity WHERE query_start IS NOT NULL AND state <> 'idle' " +
                "ORDER BY elapsed_sec DESC LIMIT 10", ct);

            var waitingLocks = await ScalarLongAsync(connection, "SELECT count(*) FROM pg_locks WHERE NOT granted", ct);
            var replicas = await ScalarLongAsync(connection, "SELECT count(*) FROM pg_stat_replication", ct);
            var deadlocks = await ScalarLongAsync(connection, "SELECT COALESCE(sum(deadlocks),0) FROM pg_stat_database", ct);
            data["locks"] = new Dictionary<string, object?> { ["waiting"] = waitingLocks };
            data["replication"] = new Dictionary<string, object?> { ["replicas"] = replicas };
            data["deadlocks"] = deadlocks;

            var blksHit = await ScalarLongAsync(connection, "SELECT COALESCE(sum(blks_hit),0) FROM pg_stat_database", ct);
            var blksRead = await ScalarLongAsync(connection, "SELECT COALESCE(sum(blks_read),0) FROM pg_stat_database", ct);
            data["cache"] = new Dictionary<string, object?>
            {
                ["blocksHit"] = blksHit,
                ["blocksRead"] = blksRead,
                ["hitRatePercent"] = HitRate(blksHit, blksRead)
            };

            data["databaseSize"] = await ScalarStringAsync(connection, "SELECT pg_size_pretty(pg_database_size(current_database()))", ct);

            stopwatch.Stop();

            var summary =
                $"PostgreSQL {serverVersionOf(version)}({database}), 连接 {totalConnections}/{maxConnections}, " +
                $"活动 {activeConnections}, 等待锁 {waitingLocks}, 复制 {replicas}, " +
                $"缓存命中 {HitRate(blksHit, blksRead)}%, 死锁 {deadlocks}, 大小 {data["databaseSize"]}";

            return new DatasourceDiagnosticsResult
            {
                Success = true,
                Summary = summary,
                Data = data,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DatasourceDiagnosticsResult
            {
                Success = false,
                Error = ex.Message,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
    }

    private static string serverVersionOf(string? version)
    {
        // "PostgreSQL 16.3 on x86_64..." -> "16.3"
        if (string.IsNullOrEmpty(version)) return "?";
        var parts = version.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : version;
    }

    private static double HitRate(long hits, long reads)
    {
        var total = hits + reads;
        return total == 0 ? 0 : Math.Round(hits * 100.0 / total, 1);
    }

    private static Dictionary<string, object?> ReadRow(NpgsqlDataReader reader, string[] columns)
    {
        var row = new Dictionary<string, object?>(columns.Length);
        for (var i = 0; i < columns.Length; i++)
        {
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            row[columns[i]] = value switch
            {
                byte[] bytes => Convert.ToBase64String(bytes),
                DateTime dt => dt.ToString("O"),
                DateTimeOffset dto => dto.UtcDateTime.ToString("O"),
                _ => value
            };
        }
        return row;
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : value.ToString();
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        var value = await ScalarStringAsync(connection, sql, ct);
        return long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static async Task<Dictionary<string, object?>> GroupCountAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        var result = new Dictionary<string, object?>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.IsDBNull(0) ? "(null)" : reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    private static async Task<List<Dictionary<string, object?>>> QueryMapAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        while (await reader.ReadAsync(ct))
            rows.Add(ReadRow(reader, columns));
        return rows;
    }
}
