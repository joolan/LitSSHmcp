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
public class PostgresDriver : IDatasourceDriver, IDatabaseExplorer, ISchemaObjectExplorer
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
        cmd.CommandText = "SELECT table_schema, table_name FROM information_schema.views " +
                          "WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1,2";
        var list = new List<DbObjectInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new DbObjectInfo { Schema = reader.GetString(0), Name = reader.GetString(1) });
        return list;
    }

    public async Task<IReadOnlyList<DbObjectInfo>> ListProceduresAsync(DataSourceConfig ds, string database, CancellationToken ct = default)
    {
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT routine_schema, routine_name FROM information_schema.routines " +
                          "WHERE routine_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1,2";
        var list = new List<DbObjectInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new DbObjectInfo { Schema = reader.GetString(0), Name = reader.GetString(1) });
        return list;
    }

    public async Task<IReadOnlyList<DbIndexInfo>> ListIndexesAsync(DataSourceConfig ds, string database, string table, string? schema = null, CancellationToken ct = default)
    {
        var schemaName = string.IsNullOrWhiteSpace(schema) ? "public" : schema;
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT i.relname AS index_name, ix.indisunique AS is_unique, ix.indisprimary AS is_primary, " +
            "       a.attname AS column_name, k.ord AS ordinal " +
            "FROM pg_class t " +
            "JOIN pg_namespace n ON n.oid = t.relnamespace " +
            "JOIN pg_index ix ON t.oid = ix.indrelid " +
            "JOIN pg_class i ON i.oid = ix.indexrelid " +
            "JOIN unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) ON true " +
            "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum " +
            "WHERE n.nspname = @s AND t.relname = @t " +
            "ORDER BY i.relname, k.ord";
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@t", table);

        var rows = new List<(string Key, bool Unique, bool Primary, int Seq, string Column)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((
                    reader.GetString(0),
                    !reader.IsDBNull(1) && reader.GetBoolean(1),
                    !reader.IsDBNull(2) && reader.GetBoolean(2),
                    Convert.ToInt32(reader.GetValue(4)),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
            }
        }
        return rows
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DbIndexInfo
            {
                Name = g.Key,
                Unique = g.First().Unique,
                Primary = g.First().Primary,
                Columns = g.OrderBy(x => x.Seq).Select(x => x.Column).ToArray()
            })
            .ToList();
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

    // ---------- 数据库工作台：对象浏览 ----------

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DataSourceConfig ds, CancellationToken ct = default)
    {
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT datname FROM pg_database WHERE datistemplate=false ORDER BY datname";
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    public async Task<IReadOnlyList<DbTableInfo>> ListTablesAsync(DataSourceConfig ds, string database, CancellationToken ct = default)
    {
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT t.table_schema, t.table_name, c.reltuples::bigint, obj_description(c.oid) " +
            "FROM information_schema.tables t " +
            "LEFT JOIN pg_namespace n ON n.nspname = t.table_schema " +
            "LEFT JOIN pg_class c ON c.relname = t.table_name AND c.relnamespace = n.oid " +
            "WHERE t.table_type='BASE TABLE' AND t.table_schema NOT IN ('pg_catalog','information_schema') " +
            "ORDER BY t.table_schema, t.table_name";
        var list = new List<DbTableInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            long? rows = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            if (rows < 0) rows = null;
            list.Add(new DbTableInfo
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                Rows = rows,
                Comment = reader.IsDBNull(3) || reader.GetString(3).Length == 0 ? null : reader.GetString(3)
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<DbColumnInfo>> ListColumnsAsync(DataSourceConfig ds, string database, string table, string? schema = null, CancellationToken ct = default)
    {
        var schemaName = string.IsNullOrWhiteSpace(schema) ? "public" : schema;
        using var session = await _connectionProvider.OpenAsync(ds, ct);

        var pk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pkCmd = session.Connection.CreateCommand())
        {
            pkCmd.CommandText =
                "SELECT kcu.column_name FROM information_schema.table_constraints tc " +
                "JOIN information_schema.key_column_usage kcu " +
                "  ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema " +
                "WHERE tc.constraint_type='PRIMARY KEY' AND tc.table_schema=@s AND tc.table_name=@t";
            pkCmd.Parameters.AddWithValue("@s", schemaName);
            pkCmd.Parameters.AddWithValue("@t", table);
            await using var pkReader = await pkCmd.ExecuteReaderAsync(ct);
            while (await pkReader.ReadAsync(ct))
                pk.Add(pkReader.GetString(0));
        }

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT cols.column_name, cols.data_type, cols.is_nullable, cols.column_default, " +
            "(SELECT pg_catalog.col_description(c.oid, a.attnum) FROM pg_catalog.pg_attribute a " +
            " JOIN pg_catalog.pg_class c ON c.oid = a.attrelid " +
            " JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace " +
            " WHERE n.nspname = @s AND c.relname = @t AND a.attname = cols.column_name) AS comment " +
            "FROM information_schema.columns cols " +
            "WHERE cols.table_schema=@s AND cols.table_name=@t ORDER BY cols.ordinal_position";
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@t", table);
        var list = new List<DbColumnInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            list.Add(new DbColumnInfo
            {
                Name = name,
                Type = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Nullable = !reader.IsDBNull(2) && reader.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase),
                PrimaryKey = pk.Contains(name),
                Default = reader.IsDBNull(3) ? null : Convert.ToString(reader.GetValue(3)),
                Comment = reader.IsDBNull(4) || reader.GetString(4).Length == 0 ? null : reader.GetString(4)
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<DbForeignKeyInfo>> ListForeignKeysAsync(DataSourceConfig ds, string database, string table, string? schema = null, CancellationToken ct = default)
    {
        var schemaName = string.IsNullOrWhiteSpace(schema) ? "public" : schema;
        using var session = await _connectionProvider.OpenAsync(ds, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT tc.constraint_name, kcu.column_name, ccu.table_schema, ccu.table_name, ccu.column_name, kcu.ordinal_position " +
            "FROM information_schema.table_constraints tc " +
            "JOIN information_schema.key_column_usage kcu " +
            "  ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema " +
            "JOIN information_schema.constraint_column_usage ccu " +
            "  ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema " +
            "WHERE tc.constraint_type='FOREIGN KEY' AND tc.table_schema=@s AND tc.table_name=@t " +
            "ORDER BY tc.constraint_name, kcu.ordinal_position";
        cmd.Parameters.AddWithValue("@s", schemaName);
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
                RefColumns = g.OrderBy(x => x.Ord).Select(x => x.RefCol).Distinct().ToArray()
            })
            .ToList();
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
