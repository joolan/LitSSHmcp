using System.Diagnostics;
using LitSSHmcp.Core.Models;
using MySqlConnector;

namespace LitSSHmcp.Core.Services.Datasource;

public class MySqlDriver : IDatasourceDriver, IDatabaseExplorer, ISchemaObjectExplorer
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

    // ---------- 数据库工作台：对象浏览 ----------

    private static readonly HashSet<string> MySqlSystemSchemas =
        new(StringComparer.OrdinalIgnoreCase) { "information_schema", "performance_schema", "sys" };

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DataSourceConfig ds, CancellationToken ct = default)
    {
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SHOW DATABASES";
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!MySqlSystemSchemas.Contains(name))
                list.Add(name);
        }
        return list.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<DbTableInfo>> ListTablesAsync(DataSourceConfig ds, string database, CancellationToken ct = default)
    {
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME, TABLE_ROWS, TABLE_COMMENT FROM information_schema.TABLES " +
                          "WHERE TABLE_SCHEMA=@db AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME";
        cmd.Parameters.AddWithValue("@db", database);
        var list = new List<DbTableInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DbTableInfo
            {
                Name = reader.GetString(0),
                Rows = reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1)),
                Comment = reader.IsDBNull(2) || reader.GetString(2).Length == 0 ? null : reader.GetString(2)
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<DbColumnInfo>> ListColumnsAsync(DataSourceConfig ds, string database, string table, string? schema = null, CancellationToken ct = default)
    {
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, COLUMN_DEFAULT, COLUMN_COMMENT " +
                          "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@t ORDER BY ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@t", table);
        var list = new List<DbColumnInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DbColumnInfo
            {
                Name = reader.GetString(0),
                Type = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Nullable = !reader.IsDBNull(2) && reader.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase),
                PrimaryKey = !reader.IsDBNull(3) && reader.GetString(3).Equals("PRI", StringComparison.OrdinalIgnoreCase),
                Default = reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4)),
                Comment = reader.IsDBNull(5) || reader.GetString(5).Length == 0 ? null : reader.GetString(5)
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<DbForeignKeyInfo>> ListForeignKeysAsync(DataSourceConfig ds, string database, string table, string? schema = null, CancellationToken ct = default)
    {
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT CONSTRAINT_NAME, COLUMN_NAME, REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME, " +
                          "REFERENCED_COLUMN_NAME, ORDINAL_POSITION FROM information_schema.KEY_COLUMN_USAGE " +
                          "WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@t AND REFERENCED_TABLE_NAME IS NOT NULL " +
                          "ORDER BY CONSTRAINT_NAME, ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@t", table);

        var rows = new List<(string Name, string Col, string RefSchema, string RefTable, string RefCol, int Ord)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    Convert.ToInt32(reader.GetValue(5))));
            }
        }

        return rows
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DbForeignKeyInfo
            {
                Name = g.Key,
                Columns = g.OrderBy(x => x.Ord).Select(x => x.Col).ToArray(),
                RefSchema = g.First().RefSchema,
                RefTable = g.First().RefTable,
                RefColumns = g.OrderBy(x => x.Ord).Select(x => x.RefCol).ToArray()
            })
            .ToList();
    }

    public async Task<IReadOnlyList<DatasourceQueryResult>> QueryMultiAsync(DataSourceConfig ds, string sql, int maxRows, CancellationToken ct = default)
    {
        var results = new List<DatasourceQueryResult>();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var session = await _connectionProvider.OpenAsync(ds, ct);
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            do
            {
                var result = new DatasourceQueryResult { Success = true };
                if (reader.FieldCount > 0)
                {
                    var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                    result.Columns = columns;
                    while (await reader.ReadAsync(ct))
                    {
                        if (result.Rows.Count >= maxRows) { result.Truncated = true; break; }
                        result.Rows.Add(ReadRow(reader, columns));
                    }
                }
                result.RowCount = result.Rows.Count;
                results.Add(result);
            } while (await reader.NextResultAsync(ct));

            stopwatch.Stop();
            if (results.Count == 0)
                results.Add(new DatasourceQueryResult { Success = true });
            foreach (var r in results)
                r.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            return results;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new[] { new DatasourceQueryResult { Success = false, Error = ex.Message, DurationMs = stopwatch.Elapsed.TotalMilliseconds } };
        }
    }

    public async Task<IReadOnlyList<DbObjectInfo>> ListViewsAsync(DataSourceConfig ds, string database, CancellationToken ct = default)
    {
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME FROM information_schema.VIEWS WHERE TABLE_SCHEMA=@db ORDER BY TABLE_NAME";
        cmd.Parameters.AddWithValue("@db", database);
        var list = new List<DbObjectInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new DbObjectInfo { Name = reader.GetString(0) });
        return list;
    }

    public async Task<IReadOnlyList<DbObjectInfo>> ListProceduresAsync(DataSourceConfig ds, string database, CancellationToken ct = default)
    {
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT ROUTINE_NAME FROM information_schema.ROUTINES " +
                          "WHERE ROUTINE_SCHEMA=@db AND ROUTINE_TYPE='PROCEDURE' ORDER BY ROUTINE_NAME";
        cmd.Parameters.AddWithValue("@db", database);
        var list = new List<DbObjectInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new DbObjectInfo { Name = reader.GetString(0) });
        return list;
    }

    public async Task<IReadOnlyList<DbIndexInfo>> ListIndexesAsync(DataSourceConfig ds, string database, string table, string? schema = null, CancellationToken ct = default)
    {
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = $"SHOW INDEX FROM `{table.Replace("`", "``")}` FROM `{database.Replace("`", "``")}`";
        var rows = new List<(string Key, bool Unique, int Seq, string Column)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(2);
                var nonUnique = Convert.ToInt32(reader.GetValue(1));
                var seq = Convert.ToInt32(reader.GetValue(3));
                var col = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                rows.Add((key, nonUnique == 0, seq, col));
            }
        }
        return rows
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DbIndexInfo
            {
                Name = g.Key,
                Unique = g.First().Unique,
                Primary = g.Key.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase),
                Columns = g.OrderBy(x => x.Seq).Select(x => x.Column).ToArray()
            })
            .ToList();
    }

    private static Dictionary<string, object?> ReadRow(MySqlDataReader reader, string[] columns)    {
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
