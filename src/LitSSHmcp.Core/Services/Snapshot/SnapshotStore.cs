using System.Data;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// 服务器快照库（独立 SQLite 文件 snapshots.db，与审计库分离）。
/// 两张表: Snapshots(快照记录) + SnapshotEvents(快照事件流水, append-only)。
/// 每台服务器按 CreatedAt 保留最近 N 份（RetentionPerServer），裁剪时事件级联删除。
/// </summary>
public interface ISnapshotStore
{
    /// <summary>建表/索引；把上次进程崩溃遗留的 Running 行改判为 Failed(interrupted)。启动时调用一次。</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>插入一条 Running 快照并记 started 事件，返回快照 Id；若该服务器已有 Running（跨进程唯一约束）返回 null。</summary>
    Task<long?> BeginAsync(string serverId, string serverName, CancellationToken ct = default);

    Task AppendEventAsync(long snapshotId, string serverId, string kind, string? collector = null, string? message = null, CancellationToken ct = default);

    Task CompleteAsync(long snapshotId, string dataJson, double durationMs, string? escalation, int collectorVersion, CancellationToken ct = default);

    Task FailAsync(long snapshotId, string? error, double durationMs, string? dataJson = null, CancellationToken ct = default);

    Task<ServerSnapshot?> GetLatestAsync(string serverId, CancellationToken ct = default);

    Task<ServerSnapshot?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>该服务器当前采集中(Running)的快照；无则 null。用于并发刷新时回传 in_progress。</summary>
    Task<ServerSnapshot?> GetRunningAsync(string serverId, CancellationToken ct = default);

    /// <summary>轻量最近列表（不含 DataJson），默认 10 条，供 get 一眼看到历史与趋势。</summary>
    Task<List<ServerSnapshot>> GetRecentAsync(string serverId, int limit = 10, CancellationToken ct = default);

    Task<List<SnapshotEvent>> GetEventsAsync(long snapshotId, int limit = 50, CancellationToken ct = default);

    /// <summary>裁剪到只保留最近 keepPerServer 份（事件级联删除）；keepPerServer&lt;=0 不限制。返回删除的快照数。</summary>
    Task<int> PruneAsync(string serverId, int keepPerServer, CancellationToken ct = default);
}

public class SnapshotStore : ISnapshotStore
{
    /// <summary>快照库结构版本（PRAGMA user_version）。结构/采集口径变更时递增并在此文件补迁移步骤；
    /// 版本不一致时当前策略为直接重建（测试期不做历史迁移，快照可再生）。</summary>
    private const int SnapshotFormatVersion = 4;

    private readonly string _dbPath;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaReady;

    public SnapshotStore() : this(null) { }

    public SnapshotStore(string? dbPath)
    {
        _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.SnapshotDb : dbPath!;
    }

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);

        // 上次进程崩溃/被杀时遗留的 Running 行：先补 failed 事件，再改判为 Failed。
        // 只应在"进程启动"时调用（MCP 启动 / 桌面 App 启动一次）；切勿放进惰性建表路径，
        // 否则另一个 Store 实例首次使用时会把本进程正在进行的采集误判为崩溃遗留。
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var orphan = connection.CreateCommand();
        orphan.CommandText = @"
INSERT INTO SnapshotEvents(SnapshotId, ServerId, Timestamp, Kind, Message)
SELECT Id, ServerId, @Now, 'failed', 'interrupted_by_restart' FROM Snapshots WHERE Status = 'Running';
UPDATE Snapshots SET Status = 'Failed', Error = 'interrupted_by_restart', CompletedAt = @Now WHERE Status = 'Running';";
        orphan.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
        await orphan.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 惰性建表（幂等、每实例一次）：MCP 与桌面 App 各自会 new 出 Store 实例，调用方不一定记得先 InitializeAsync；
    /// 所有读写入口都先确保结构就绪，避免 "no such table: Snapshots"。此处只建表, 不做孤儿重置。
    /// </summary>
    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaReady)
            return;

        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady)
                return;

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(ct);

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;";
                await pragma.ExecuteNonQueryAsync(ct);
            }

            await using (var reset = connection.CreateCommand())
            {
                // 版本不一致（结构升级 / 采集口径变化）时直接重建：快照是可再生数据，测试期不做历史迁移
                // （新增采集字段都在 DataJson(JSON) 内，本身就无需迁移；这里顺带清掉旧口径的历史快照）。
                reset.CommandText = "PRAGMA user_version;";
                var version = Convert.ToInt32(await reset.ExecuteScalarAsync(ct));
                if (version != SnapshotFormatVersion)
                {
                    await using var drop = connection.CreateCommand();
                    drop.CommandText = "DROP TABLE IF EXISTS SnapshotEvents; DROP TABLE IF EXISTS Snapshots;";
                    await drop.ExecuteNonQueryAsync(ct);

                    await using var set = connection.CreateCommand();
                    set.CommandText = $"PRAGMA user_version = {SnapshotFormatVersion};";
                    await set.ExecuteNonQueryAsync(ct);
                }
            }

            await using (var create = connection.CreateCommand())
            {
                create.CommandText = @"
CREATE TABLE IF NOT EXISTS Snapshots (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ServerId        TEXT NOT NULL,
    ServerName      TEXT NOT NULL DEFAULT '',
    CreatedAt       TEXT NOT NULL,
    CompletedAt     TEXT,
    Status          TEXT NOT NULL,
    DurationMs      REAL NOT NULL DEFAULT 0,
    Error           TEXT,
    Escalation      TEXT,
    CollectorVersion INTEGER NOT NULL DEFAULT 1,
    DataJson        TEXT
);
CREATE INDEX IF NOT EXISTS IX_Snapshots_ServerId ON Snapshots(ServerId, CreatedAt DESC);
CREATE INDEX IF NOT EXISTS IX_Snapshots_Status ON Snapshots(Status);
-- 跨进程单飞: 同一服务器最多一行 Running。App 与 MCP 各自进程内存锁不互通, 用此约束兜底并发。
CREATE UNIQUE INDEX IF NOT EXISTS UX_Snapshots_Running ON Snapshots(ServerId) WHERE Status = 'Running';

CREATE TABLE IF NOT EXISTS SnapshotEvents (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    SnapshotId  INTEGER NOT NULL,
    ServerId    TEXT NOT NULL,
    Timestamp   TEXT NOT NULL,
    Kind        TEXT NOT NULL,
    Collector   TEXT,
    Message     TEXT
);
CREATE INDEX IF NOT EXISTS IX_SnapshotEvents_SnapshotId ON SnapshotEvents(SnapshotId, Id);";
                await create.ExecuteNonQueryAsync(ct);
            }

            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    public async Task<long?> BeginAsync(string serverId, string serverName, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            long id;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"INSERT INTO Snapshots(ServerId, ServerName, CreatedAt, Status, CollectorVersion)
VALUES (@ServerId, @ServerName, @CreatedAt, @Status, @CollectorVersion); SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("@ServerId", serverId);
                insert.Parameters.AddWithValue("@ServerName", serverName ?? string.Empty);
                insert.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("O"));
                insert.Parameters.AddWithValue("@Status", SnapshotStatus.Running.ToString());
                insert.Parameters.AddWithValue("@CollectorVersion", SnapshotMeta.CollectorVersion);
                id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
            }

            await using (var ev = connection.CreateCommand())
            {
                ev.Transaction = tx;
                ev.CommandText = @"INSERT INTO SnapshotEvents(SnapshotId, ServerId, Timestamp, Kind, Message)
VALUES (@SnapshotId, @ServerId, @Timestamp, 'started', @Message);";
                ev.Parameters.AddWithValue("@SnapshotId", id);
                ev.Parameters.AddWithValue("@ServerId", serverId);
                ev.Parameters.AddWithValue("@Timestamp", DateTime.UtcNow.ToString("O"));
                ev.Parameters.AddWithValue("@Message", serverName);
                await ev.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)   // SQLITE_CONSTRAINT: 已有 Running
        {
            return null;
        }
    }

    public async Task AppendEventAsync(long snapshotId, string serverId, string kind, string? collector = null, string? message = null, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"INSERT INTO SnapshotEvents(SnapshotId, ServerId, Timestamp, Kind, Collector, Message)
VALUES (@SnapshotId, @ServerId, @Timestamp, @Kind, @Collector, @Message);";
        cmd.Parameters.AddWithValue("@SnapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@ServerId", serverId);
        cmd.Parameters.AddWithValue("@Timestamp", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@Kind", kind);
        cmd.Parameters.AddWithValue("@Collector", (object?)collector ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Message", (object?)Truncate(message, 2000) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteAsync(long snapshotId, string dataJson, double durationMs, string? escalation, int collectorVersion, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"UPDATE Snapshots
SET Status = 'Succeeded', CompletedAt = @CompletedAt, DurationMs = @DurationMs,
    Escalation = @Escalation, CollectorVersion = @CollectorVersion, DataJson = @DataJson, Error = NULL
WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@CompletedAt", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@DurationMs", durationMs);
        cmd.Parameters.AddWithValue("@Escalation", (object?)escalation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CollectorVersion", collectorVersion);
        cmd.Parameters.AddWithValue("@DataJson", dataJson);
        cmd.Parameters.AddWithValue("@Id", snapshotId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task FailAsync(long snapshotId, string? error, double durationMs, string? dataJson = null, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        // 失败也保留已采集到的 section（部分数据对排障有价值）；未提供时保留原值。
        cmd.CommandText = @"UPDATE Snapshots
SET Status = 'Failed', CompletedAt = @CompletedAt, DurationMs = @DurationMs, Error = @Error,
    DataJson = COALESCE(@DataJson, DataJson)
WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@CompletedAt", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@DurationMs", durationMs);
        cmd.Parameters.AddWithValue("@Error", (object?)Truncate(error, 4000) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DataJson", (object?)dataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Id", snapshotId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ServerSnapshot?> GetLatestAsync(string serverId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT * FROM Snapshots WHERE ServerId = @ServerId
ORDER BY CreatedAt DESC, Id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("@ServerId", serverId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRow(reader) : null;
    }

    public async Task<ServerSnapshot?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Snapshots WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRow(reader) : null;
    }

    public async Task<ServerSnapshot?> GetRunningAsync(string serverId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT * FROM Snapshots WHERE ServerId = @ServerId AND Status = 'Running'
ORDER BY Id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("@ServerId", serverId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRow(reader) : null;
    }

    public async Task<List<ServerSnapshot>> GetRecentAsync(string serverId, int limit = 10, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var result = new List<ServerSnapshot>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT Id, ServerId, ServerName, CreatedAt, CompletedAt, Status, DurationMs, Error, Escalation, CollectorVersion
FROM Snapshots WHERE ServerId = @ServerId
ORDER BY CreatedAt DESC, Id DESC LIMIT @Limit;";
        cmd.Parameters.AddWithValue("@ServerId", serverId);
        cmd.Parameters.AddWithValue("@Limit", Math.Clamp(limit, 1, 100));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadRow(reader));
        return result;
    }

    public async Task<List<SnapshotEvent>> GetEventsAsync(long snapshotId, int limit = 50, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var result = new List<SnapshotEvent>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT Id, SnapshotId, ServerId, Timestamp, Kind, Collector, Message
FROM SnapshotEvents WHERE SnapshotId = @SnapshotId
ORDER BY Id ASC LIMIT @Limit;";
        cmd.Parameters.AddWithValue("@SnapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@Limit", Math.Clamp(limit, 1, 500));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new SnapshotEvent
            {
                Id = reader.GetInt64(0),
                SnapshotId = reader.GetInt64(1),
                ServerId = reader.GetString(2),
                Timestamp = reader.GetString(3),
                Kind = reader.GetString(4),
                Collector = reader.IsDBNull(5) ? null : reader.GetString(5),
                Message = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }
        return result;
    }

    public async Task<int> PruneAsync(string serverId, int keepPerServer, CancellationToken ct = default)
    {
        if (keepPerServer <= 0)
            return 0;

        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        // 先删被裁快照的事件，再删快照本身（两步共用同一"保留集合"条件）。
        await using (var ev = connection.CreateCommand())
        {
            ev.CommandText = @"
DELETE FROM SnapshotEvents WHERE SnapshotId IN (
    SELECT Id FROM Snapshots WHERE ServerId = @ServerId
    AND Id NOT IN (SELECT Id FROM Snapshots WHERE ServerId = @ServerId
                   ORDER BY CreatedAt DESC, Id DESC LIMIT @Keep));";
            ev.Parameters.AddWithValue("@ServerId", serverId);
            ev.Parameters.AddWithValue("@Keep", keepPerServer);
            await ev.ExecuteNonQueryAsync(ct);
        }

        await using var del = connection.CreateCommand();
        del.CommandText = @"
DELETE FROM Snapshots WHERE ServerId = @ServerId
AND Id NOT IN (SELECT Id FROM Snapshots WHERE ServerId = @ServerId
               ORDER BY CreatedAt DESC, Id DESC LIMIT @Keep);";
        del.Parameters.AddWithValue("@ServerId", serverId);
        del.Parameters.AddWithValue("@Keep", keepPerServer);
        return await del.ExecuteNonQueryAsync(ct);
    }

    private static ServerSnapshot ReadRow(SqliteDataReader reader)
    {
        // 列序与建表语句一致；recent 查询缺少 DataJson（末列）时置 null。
        var hasData = reader.FieldCount > 10;
        return new ServerSnapshot
        {
            Id = reader.GetInt64(0),
            ServerId = reader.GetString(1),
            ServerName = reader.GetString(2),
            CreatedAt = reader.GetString(3),
            CompletedAt = reader.IsDBNull(4) ? null : reader.GetString(4),
            Status = ParseStatus(reader.GetString(5)),
            DurationMs = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
            Error = reader.IsDBNull(7) ? null : reader.GetString(7),
            Escalation = reader.IsDBNull(8) ? null : reader.GetString(8),
            CollectorVersion = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
            DataJson = hasData && !reader.IsDBNull(10) ? reader.GetString(10) : null
        };
    }

    private static SnapshotStatus ParseStatus(string value) =>
        Enum.TryParse(value, ignoreCase: true, out SnapshotStatus status) ? status : SnapshotStatus.Failed;

    private static string? Truncate(string? value, int max) =>
        value is { Length: > 0 } ? (value.Length <= max ? value : value[..max] + "...") : value;
}
