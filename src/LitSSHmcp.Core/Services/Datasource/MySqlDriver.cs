using System.Diagnostics;
using LitSSHmcp.Core.Models;
using MySqlConnector;

namespace LitSSHmcp.Core.Services.Datasource;

public class MySqlDriver : IDatasourceDriver
{
    private readonly IMySqlConnectionProvider _connectionProvider;

    public string Type => "mysql";

    public MySqlDriver(IMySqlConnectionProvider connectionProvider)
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
                cmd.CommandText = "SELECT VERSION()";
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
        if (keyword is "explain")
            return await QueryAsync(ds, normalized, 100, ct);

        return await QueryAsync(ds, $"EXPLAIN {normalized}", 100, ct);
    }

    public async Task<DatasourceDiagnosticsResult> DiagnoseAsync(DataSourceConfig ds, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var session = await _connectionProvider.OpenAsync(ds, ct);
            var data = new Dictionary<string, object?>();

            var status = await CollectGlobalStatusAsync(session.Connection, ct);
            var variables = await CollectGlobalVariablesAsync(session.Connection, ct);

            data["version"] = variables.GetValueOrDefault("version");
            data["uptimeSeconds"] = ParseLong(status.GetValueOrDefault("Uptime"));
            data["connections"] = new Dictionary<string, object?>
            {
                ["current"] = ParseLong(status.GetValueOrDefault("Threads_connected")),
                ["max"] = ParseLong(variables.GetValueOrDefault("max_connections")),
                ["rejected"] = ParseLong(status.GetValueOrDefault("Connection_errors_max_connections")),
                ["errors"] = ParseLong(status.GetValueOrDefault("Connection_errors"))
            };
            data["queries"] = new Dictionary<string, object?>
            {
                ["total"] = ParseLong(status.GetValueOrDefault("Questions")),
                ["slowQueries"] = ParseLong(status.GetValueOrDefault("Slow_queries")),
                ["slowQueryLogEnabled"] = variables.GetValueOrDefault("slow_query_log"),
                ["longQueryTime"] = variables.GetValueOrDefault("long_query_time")
            };
            data["threads"] = new Dictionary<string, object?>
            {
                ["running"] = ParseLong(status.GetValueOrDefault("Threads_running")),
                ["cached"] = ParseLong(status.GetValueOrDefault("Threads_cached"))
            };
            data["innodb"] = new Dictionary<string, object?>
            {
                ["rowLockWaits"] = ParseLong(status.GetValueOrDefault("Innodb_row_lock_current_waits")),
                ["rowLockTimeAvgMs"] = ParseLong(status.GetValueOrDefault("Innodb_row_lock_time_avg")),
                ["bufferPoolPagesDirty"] = ParseLong(status.GetValueOrDefault("Innodb_buffer_pool_pages_dirty"))
            };
            data["tempTables"] = new Dictionary<string, object?>
            {
                ["created"] = ParseLong(status.GetValueOrDefault("Created_tmp_tables")),
                ["createdDisk"] = ParseLong(status.GetValueOrDefault("Created_tmp_disk_tables"))
            };
            data["replication"] = await CollectReplicationAsync(session.Connection, ct);
            data["processlist"] = await CollectProcesslistSummaryAsync(session.Connection, ct);

            stopwatch.Stop();

            var connected = ParseLong(status.GetValueOrDefault("Threads_connected"));
            var maxConn = ParseLong(variables.GetValueOrDefault("max_connections"));
            var slow = ParseLong(status.GetValueOrDefault("Slow_queries"));
            var summary =
                $"运行{ParseLong(status.GetValueOrDefault("Uptime"))}s, 连接 {connected}/{maxConn}, " +
                $"慢查询 {slow}, 活跃SQL {ParseLong(status.GetValueOrDefault("Threads_running"))}";

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

    private static Dictionary<string, object?> ReadRow(MySqlDataReader reader, string[] columns)
    {
        var row = new Dictionary<string, object?>(columns.Length);
        for (var i = 0; i < columns.Length; i++)
        {
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            row[columns[i]] = value switch
            {
                byte[] bytes => Convert.ToBase64String(bytes),
                DateTime dt => dt.ToString("O"),
                _ => value
            };
        }
        return row;
    }

    private static async Task<Dictionary<string, string>> CollectGlobalStatusAsync(MySqlConnection connection, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SHOW GLOBAL STATUS";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    private static async Task<Dictionary<string, string>> CollectGlobalVariablesAsync(MySqlConnection connection, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SHOW GLOBAL VARIABLES";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    private static async Task<object?> CollectReplicationAsync(MySqlConnection connection, CancellationToken ct)
    {
        foreach (var query in new[] { "SHOW REPLICA STATUS", "SHOW SLAVE STATUS" })
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = query;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!reader.HasRows) return null;

                await reader.ReadAsync(ct);
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                var row = ReadRow(reader, columns);
                return new Dictionary<string, object?>
                {
                    ["sourceHost"] = GetValueOrDefault(row, "Source_Host", "Master_Host"),
                    ["replicaIoRunning"] = GetValueOrDefault(row, "Replica_IO_Running", "Slave_IO_Running"),
                    ["replicaSqlRunning"] = GetValueOrDefault(row, "Replica_SQL_Running", "Slave_SQL_Running"),
                    ["secondsBehind"] = GetValueOrDefault(row, "Seconds_Behind_Source", "Seconds_Behind_Master"),
                    ["lastIoError"] = GetValueOrDefault(row, "Last_IO_Error"),
                    ["lastSqlError"] = GetValueOrDefault(row, "Last_SQL_Error")
                };
            }
            catch
            {
                // 权限不足或版本不支持时尝试下一种语法
            }
        }
        return null;
    }

    private static async Task<Dictionary<string, object?>> CollectProcesslistSummaryAsync(MySqlConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SHOW FULL PROCESSLIST";
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var total = 0;
        var byUser = new Dictionary<string, int>();
        var byState = new Dictionary<string, int>();
        var byDb = new Dictionary<string, int>();
        var longest = new List<Dictionary<string, object?>>();

        while (await reader.ReadAsync(ct))
        {
            total++;
            // SHOW FULL PROCESSLIST 列序: Id, User, Host, db, Command, Time, State, Info
            // (此前把 Host 当 db、把 Command[Query/Daemon/Sleep...] 当 Time 解析 → Convert.ToInt64("Daemon") 抛格式错)
            var user = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var db = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var time = reader.IsDBNull(5) ? 0L : ToLong(reader.GetValue(5));
            var state = reader.IsDBNull(6) ? "" : reader.GetString(6);
            var info = reader.IsDBNull(7) ? "" : reader.GetString(7);

            byUser[user] = byUser.GetValueOrDefault(user) + 1;
            byDb[db] = byDb.GetValueOrDefault(db) + 1;
            var stateKey = string.IsNullOrWhiteSpace(state) ? "(query)" : state;
            byState[stateKey] = byState.GetValueOrDefault(stateKey) + 1;

            if (time > 0)
            {
                longest.Add(new Dictionary<string, object?>
                {
                    ["id"] = reader.GetInt64(0),
                    ["user"] = user,
                    ["db"] = db,
                    ["timeSec"] = time,
                    ["state"] = state,
                    ["sql"] = info.Length > 500 ? info[..500] + "..." : info
                });
            }
        }

        return new Dictionary<string, object?>
        {
            ["total"] = total,
            ["byUser"] = byUser,
            ["byDatabase"] = byDb.Where(kv => !string.IsNullOrEmpty(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
            ["byState"] = byState,
            ["longestRunning"] = longest.OrderByDescending(d => (long)(d["timeSec"] ?? 0)).Take(10).ToList()
        };
    }

    private static object? GetValueOrDefault(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
            if (row.TryGetValue(key, out var value))
                return value;
        return null;
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, out var result) ? result : 0;

    /// <summary>把任意数值/可解析对象安全转为 long（非数值返回 0，绝不抛异常）。</summary>
    private static long ToLong(object? value) => value switch
    {
        null => 0L,
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        ulong ul => (long)ul,
        uint ui => ui,
        decimal d => (long)d,
        double db => (long)db,
        float f => (long)f,
        _ => long.TryParse(value.ToString(), out var parsed) ? parsed : 0L
    };
}
