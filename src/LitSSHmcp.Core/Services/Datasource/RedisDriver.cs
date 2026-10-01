using System.Diagnostics;
using System.Globalization;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.Core.Services.Datasource;

/// <summary>
/// Redis 数据源驱动：连通性测试、只读命令查询、写命令执行、运行期诊断。
/// 复用 <see cref="IDatasourceDriver"/> 抽象，因此 mcp_self_check / datasource_test_connection /
/// 界面"测试连接" / 拓扑发现都能直接支持 redis 类型数据源。
/// </summary>
public class RedisDriver : IDatasourceDriver
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly IRedisConnectionProvider _connectionProvider;

    public string Type => "redis";

    public RedisDriver(IRedisConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public async Task<DatasourceTestResult> TestAsync(DataSourceConfig ds, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var session = await _connectionProvider.OpenAsync(ds, ct);
            using var timeout = CreateTimeout(ct);

            var pong = await session.Client.ExecuteAsync(new[] { "PING" }, timeout.Token);
            if (pong.IsError)
                throw new InvalidOperationException($"PING 失败: {pong.ErrorMessage}");

            string? version = null;
            var info = await session.Client.ExecuteAsync(new[] { "INFO", "server" }, timeout.Token);
            if (!info.IsError && ParseInfo(info.ToDisplayString()).TryGetValue("redis_version", out var parsed))
                version = $"redis {parsed}";

            stopwatch.Stop();
            return new DatasourceTestResult
            {
                Success = true,
                Message = "连接成功",
                Version = version ?? "PONG",
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

    public async Task<DatasourceQueryResult> QueryAsync(DataSourceConfig ds, string command, int maxRows, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var args = RedisCommandParser.Split(command);
            if (RedisCommandPolicy.Classify(command) != RedisCommandKind.ReadOnly)
            {
                stopwatch.Stop();
                return new DatasourceQueryResult
                {
                    Success = false,
                    DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                    Error = $"非只读命令({RedisCommandPolicy.CommandName(command)})。只读查询仅允许白名单内的读命令，" +
                            "写/管理命令请使用 redis_execute（会经过安全过滤与用户审批）。"
                };
            }

            using var session = await _connectionProvider.OpenAsync(ds, ct);
            using var timeout = CreateTimeout(ct);

            var reply = await session.Client.ExecuteAsync(args, timeout.Token);
            stopwatch.Stop();

            if (reply.IsError)
            {
                return new DatasourceQueryResult
                {
                    Success = false,
                    DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                    Error = reply.ErrorMessage
                };
            }

            var (columns, rows, truncated) = ToTable(reply, maxRows);
            return new DatasourceQueryResult
            {
                Success = true,
                Columns = columns,
                Rows = rows,
                RowCount = rows.Count,
                Truncated = truncated,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds
            };
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

    public async Task<DatasourceExecuteResult> ExecuteAsync(DataSourceConfig ds, string command, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var args = RedisCommandParser.Split(command);
            if (RedisCommandPolicy.Classify(command) == RedisCommandKind.Blocked)
            {
                stopwatch.Stop();
                return new DatasourceExecuteResult
                {
                    Success = false,
                    DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                    Error = $"命令 {RedisCommandPolicy.CommandName(command)} 被安全策略禁止执行" +
                            "（清库/关服/换主从/加载模块/阻塞连接类命令）。被禁止的命令不会以任何方式执行。"
                };
            }

            using var session = await _connectionProvider.OpenAsync(ds, ct);
            using var timeout = CreateTimeout(ct);

            var reply = await session.Client.ExecuteAsync(args, timeout.Token);
            stopwatch.Stop();

            if (reply.IsError)
            {
                return new DatasourceExecuteResult
                {
                    Success = false,
                    DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                    Error = reply.ErrorMessage
                };
            }

            return new DatasourceExecuteResult
            {
                Success = true,
                RowsAffected = reply.Kind == RedisValueKind.Integer ? reply.Integer : 0,
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

    public Task<DatasourceQueryResult> ExplainAsync(DataSourceConfig ds, string sql, CancellationToken ct = default) =>
        Task.FromResult(new DatasourceQueryResult
        {
            Success = false,
            Error = "Redis 没有 EXPLAIN。请用 redis_diagnostics 查看运行状态，或用 redis_read 执行 INFO/SLOWLOG 等只读命令。"
        });

    public async Task<DatasourceDiagnosticsResult> DiagnoseAsync(DataSourceConfig ds, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var session = await _connectionProvider.OpenAsync(ds, ct);
            using var timeout = CreateTimeout(ct);
            var client = session.Client;

            var data = new Dictionary<string, object?>();

            var infoReply = await client.ExecuteAsync(new[] { "INFO" }, timeout.Token);
            if (infoReply.IsError)
                throw new InvalidOperationException($"INFO 失败: {infoReply.ErrorMessage}");

            var info = ParseInfo(infoReply.ToDisplayString());

            data["version"] = GetValue(info, "redis_version");
            data["mode"] = GetValue(info, "redis_mode");
            data["uptimeSeconds"] = ParseLong(GetValue(info, "uptime_in_seconds"));
            data["server"] = new Dictionary<string, object?>
            {
                ["os"] = GetValue(info, "os"),
                ["tcpPort"] = GetValue(info, "tcp_port"),
                ["processId"] = GetValue(info, "process_id"),
                ["configFile"] = GetValue(info, "config_file"),
                ["executable"] = GetValue(info, "executable")
            };
            data["memory"] = new Dictionary<string, object?>
            {
                ["usedBytes"] = ParseLong(GetValue(info, "used_memory")),
                ["usedHuman"] = GetValue(info, "used_memory_human"),
                ["rssBytes"] = ParseLong(GetValue(info, "used_memory_rss")),
                ["fragmentationRatio"] = ParseDouble(GetValue(info, "mem_fragmentation_ratio")),
                ["maxBytes"] = ParseLong(GetValue(info, "maxmemory")),
                ["maxMemoryPolicy"] = GetValue(info, "maxmemory_policy"),
                ["peakBytes"] = ParseLong(GetValue(info, "used_memory_peak"))
            };
            data["clients"] = new Dictionary<string, object?>
            {
                ["connected"] = ParseLong(GetValue(info, "connected_clients")),
                ["blocked"] = ParseLong(GetValue(info, "blocked_clients")),
                ["rejected"] = ParseLong(GetValue(info, "rejected_connections")),
                ["totalReceived"] = ParseLong(GetValue(info, "total_connections_received"))
            };
            data["stats"] = new Dictionary<string, object?>
            {
                ["opsPerSec"] = ParseLong(GetValue(info, "instantaneous_ops_per_sec")),
                ["totalCommands"] = ParseLong(GetValue(info, "total_commands_processed")),
                ["evictedKeys"] = ParseLong(GetValue(info, "evicted_keys")),
                ["expiredKeys"] = ParseLong(GetValue(info, "expired_keys")),
                ["keyspaceHits"] = ParseLong(GetValue(info, "keyspace_hits")),
                ["keyspaceMisses"] = ParseLong(GetValue(info, "keyspace_misses")),
                ["hitRatePercent"] = HitRate(ParseLong(GetValue(info, "keyspace_hits")), ParseLong(GetValue(info, "keyspace_misses")))
            };
            data["persistence"] = new Dictionary<string, object?>
            {
                ["rdbLastSaveTime"] = ToIso(ParseLong(GetValue(info, "rdb_last_save_time"))),
                ["rdbChangesSinceLastSave"] = ParseLong(GetValue(info, "rdb_changes_since_last_save")),
                ["rdbLastBgsaveStatus"] = GetValue(info, "rdb_last_bgsave_status"),
                ["aofEnabled"] = GetValue(info, "aof_enabled"),
                ["aofLastWriteStatus"] = GetValue(info, "aof_last_write_status")
            };
            data["replication"] = new Dictionary<string, object?>
            {
                ["role"] = GetValue(info, "role"),
                ["connectedReplicas"] = ParseLong(GetValue(info, "connected_slaves")),
                ["masterReplOffset"] = ParseLong(GetValue(info, "master_repl_offset")),
                ["replicaReplOffset"] = ParseLong(GetValue(info, "slave_repl_offset"))
            };
            data["keyspace"] = ParseKeyspace(infoReply.ToDisplayString());

            var dbSize = await client.ExecuteAsync(new[] { "DBSIZE" }, timeout.Token);
            data["totalKeys"] = dbSize.Kind == RedisValueKind.Integer ? dbSize.Integer : ParseLong(dbSize.ToDisplayString());

            data["clientsList"] = await TryAsync(async () =>
            {
                var reply = await client.ExecuteAsync(new[] { "CLIENT", "LIST" }, timeout.Token);
                if (reply.IsError) throw new InvalidOperationException(reply.ErrorMessage);
                return SummarizeClients(reply.ToDisplayString());
            }, timeout.Token);

            data["slowlog"] = await TryAsync(async () =>
            {
                var reply = await client.ExecuteAsync(new[] { "SLOWLOG", "GET", "5" }, timeout.Token);
                if (reply.IsError) throw new InvalidOperationException(reply.ErrorMessage);
                return SummarizeSlowlog(reply);
            }, timeout.Token);

            data["config"] = await TryAsync(async () =>
            {
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var pattern in new[] { "maxmemory*", "timeout", "hz" })
                {
                    var reply = await client.ExecuteAsync(new[] { "CONFIG", "GET", pattern }, timeout.Token);
                    if (reply.IsError) continue;
                    foreach (var (key, value) in Pairs(reply))
                        values[key] = value;
                }
                return values;
            }, timeout.Token);

            stopwatch.Stop();

            var summary = BuildSummary(data);
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

    private static CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CommandTimeout);
        return cts;
    }

    private static (string[] Columns, List<Dictionary<string, object?>> Rows, bool Truncated) ToTable(RedisValue reply, int maxRows)
    {
        if (reply.IsNull)
            return (new[] { "value" }, new List<Dictionary<string, object?>>(), false);

        if (reply.Kind != RedisValueKind.Array)
        {
            var rows = new List<Dictionary<string, object?>>
            {
                new() { ["value"] = RedisValueFormatter.ToCompactString(reply) }
            };
            return (new[] { "value" }, rows, false);
        }

        var columns = new[] { "index", "value" };
        var table = new List<Dictionary<string, object?>>();
        var truncated = false;

        var items = reply.Items ?? Array.Empty<RedisValue>();
        for (var i = 0; i < items.Count; i++)
        {
            if (table.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            table.Add(new Dictionary<string, object?>
            {
                ["index"] = i.ToString(CultureInfo.InvariantCulture),
                ["value"] = RedisValueFormatter.ToCompactString(items[i])
            });
        }

        return (columns, table, truncated);
    }

    private static Dictionary<string, string> ParseInfo(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains(':')) continue;
            var separator = line.IndexOf(':');
            result[line[..separator]] = line[(separator + 1)..].Trim();
        }
        return result;
    }

    private static Dictionary<string, object?> ParseKeyspace(string text)
    {
        var result = new Dictionary<string, object?>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("db", StringComparison.OrdinalIgnoreCase) || !line.Contains(':')) continue;

            var separator = line.IndexOf(':');
            var db = line[..separator];
            var fields = new Dictionary<string, object?>();
            foreach (var part in line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length == 2) fields[kv[0]] = long.TryParse(kv[1], out var value) ? value : kv[1];
            }
            result[db] = fields;
        }
        return result;
    }

    private static List<Dictionary<string, object?>> SummarizeClients(string text)
    {
        var clients = new List<Dictionary<string, object?>>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!raw.StartsWith("id=", StringComparison.Ordinal)) continue;

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2) fields[kv[0]] = kv[1];
            }

            clients.Add(new Dictionary<string, object?>
            {
                ["id"] = fields.GetValueOrDefault("id"),
                ["addr"] = fields.GetValueOrDefault("addr"),
                ["name"] = fields.GetValueOrDefault("name"),
                ["ageSec"] = ParseLong(fields.GetValueOrDefault("age")),
                ["idleSec"] = ParseLong(fields.GetValueOrDefault("idle")),
                ["cmd"] = fields.GetValueOrDefault("cmd"),
                ["db"] = fields.GetValueOrDefault("db")
            });
        }

        return clients
            .OrderByDescending(c => c["idleSec"] as long? ?? 0)
            .Take(10)
            .ToList();
    }

    private static List<Dictionary<string, object?>> SummarizeSlowlog(RedisValue reply)
    {
        var entries = new List<Dictionary<string, object?>>();
        foreach (var item in reply.Items ?? Array.Empty<RedisValue>())
        {
            var parts = item.Items ?? Array.Empty<RedisValue>();
            if (parts.Count < 4) continue;

            entries.Add(new Dictionary<string, object?>
            {
                ["id"] = parts[0].ToDisplayString(),
                ["timestamp"] = ToIso(ParseLong(parts[1].ToDisplayString())),
                ["durationUs"] = ParseLong(parts[2].ToDisplayString()),
                ["command"] = Truncate(string.Join(' ', parts[3].Items?.Select(p => p.ToDisplayString()) ?? Array.Empty<string>()), 300),
                ["addr"] = parts.Count > 4 ? parts[4].ToDisplayString() : null
            });
        }
        return entries;
    }

    private static IEnumerable<(string Key, string Value)> Pairs(RedisValue reply)
    {
        var items = reply.Items ?? Array.Empty<RedisValue>();
        for (var i = 0; i + 1 < items.Count; i += 2)
            yield return (items[i].ToDisplayString(), items[i + 1].ToDisplayString());
    }

    private static async Task<object?> TryAsync(Func<Task<object?>> action, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSummary(Dictionary<string, object?> data)
    {
        static string Field(Dictionary<string, object?> section, string key) =>
            section.TryGetValue(key, out var value) && value != null ? value.ToString() ?? "" : "";

        var memory = data.GetValueOrDefault("memory") as Dictionary<string, object?> ?? new();
        var clients = data.GetValueOrDefault("clients") as Dictionary<string, object?> ?? new();
        var stats = data.GetValueOrDefault("stats") as Dictionary<string, object?> ?? new();
        var replication = data.GetValueOrDefault("replication") as Dictionary<string, object?> ?? new();

        var slowlog = data.GetValueOrDefault("slowlog") as List<Dictionary<string, object?>> ?? new();

        return $"Redis {Field(data, "version")}({Field(data, "mode")}) " +
               $"运行 {Field(data, "uptimeSeconds")}s, " +
               $"内存 {Field(memory, "usedHuman")}(峰值{Field(memory, "peakBytes")}B, 上限{Field(memory, "maxBytes")}B/{Field(memory, "maxMemoryPolicy")}), " +
               $"客户端 {Field(clients, "connected")}(阻塞{Field(clients, "blocked")}, 拒绝{Field(clients, "rejected")}), " +
               $"{Field(stats, "opsPerSec")} ops/s, 命中率 {Field(stats, "hitRatePercent")}%, " +
               $"淘汰 {Field(stats, "evictedKeys")}, 总键数 {Field(data, "totalKeys")}, " +
               $"role={Field(replication, "role")}, 慢日志 {slowlog.Count} 条";
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, out var result) ? result : 0;

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? Math.Round(result, 2) : 0;

    private static double HitRate(long hits, long misses)
    {
        var total = hits + misses;
        return total == 0 ? 0 : Math.Round(hits * 100.0 / total, 1);
    }

    private static string? ToIso(long unixSeconds) =>
        unixSeconds <= 0
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("O");

    private static string? GetValue(Dictionary<string, string> info, string key) =>
        info.TryGetValue(key, out var value) ? value : null;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
