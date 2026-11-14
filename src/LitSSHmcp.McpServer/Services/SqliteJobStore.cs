using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.McpServer.Services;

/// <summary>Job 状态机（详见 maybe-to/2026-11-13-长任务统一Job模型设计.md §3.2）。</summary>
public static class JobStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Orphaned = "orphaned";

    public static bool IsTerminal(string state) =>
        state is Succeeded or Failed or Cancelled or Orphaned;
}

/// <summary>Job 事件类型。</summary>
public static class JobEventKinds
{
    public const string Log = "log";
    public const string Progress = "progress";
    public const string Phase = "phase";
    public const string Result = "result";
}

/// <summary>跨进程共享的 Job 持久化状态（jobs.db）。</summary>
public sealed class JobState
{
    public string Id { get; set; } = "";
    public string Operation { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public string State { get; set; } = JobStates.Pending;
    public string OwnerUser { get; set; } = "";
    public string SessionId { get; set; } = "";
    public int ProcessId { get; set; }
    public double Progress { get; set; }
    public int ProgressTotal { get; set; }
    public int ProgressDone { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorStatus { get; set; }
    public string? ErrorText { get; set; }
    public bool Partial { get; set; }
    public bool CancelRequested { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>单条 Job 事件（(JobId, Seq) 断点续读）；结构化列兼容旧库/旧事件。</summary>
public sealed record JobEvent(long Seq, string Kind, string Message, DateTimeOffset CreatedAt,
    string? Server = null, string? Phase = null, int? Done = null, int? Total = null, string? Status = null);

/// <summary>
/// Job 落盘存储：jobs.db（mcp_jobs + mcp_job_events），WAL 模式跨进程共享。
/// 状态变更/心跳/取消标记/事件追加均通过本 store；模式沿用 SqliteFleetPlanStore。
/// </summary>
public sealed class SqliteJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dbPath;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteJobStore(string? dbPath = null) =>
        _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.JobsDb : dbPath!;

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS mcp_jobs (
                    Id            TEXT PRIMARY KEY,
                    Operation     TEXT NOT NULL,
                    ArgumentsJson TEXT NOT NULL,
                    State         TEXT NOT NULL,
                    OwnerUser     TEXT NOT NULL,
                    SessionId     TEXT NOT NULL,
                    ProcessId     INTEGER NOT NULL,
                    Progress      REAL NOT NULL DEFAULT 0,
                    ProgressTotal INTEGER NOT NULL DEFAULT 0,
                    ProgressDone  INTEGER NOT NULL DEFAULT 0,
                    ResultJson    TEXT,
                    ErrorStatus   TEXT,
                    ErrorText     TEXT,
                    Partial       INTEGER NOT NULL DEFAULT 0,
                    CancelRequested INTEGER NOT NULL DEFAULT 0,
                    CreatedAt     TEXT NOT NULL,
                    HeartbeatAt   TEXT,
                    FinishedAt    TEXT
                );
                CREATE INDEX IF NOT EXISTS IX_mcp_jobs_owner
                    ON mcp_jobs(OwnerUser, SessionId, CreatedAt DESC);
                CREATE TABLE IF NOT EXISTS mcp_job_events (
                    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    JobId     TEXT NOT NULL,
                    Seq       INTEGER NOT NULL,
                    Kind      TEXT NOT NULL,
                    Message   TEXT NOT NULL,
                    Server    TEXT NOT NULL DEFAULT '',
                    Phase     TEXT NOT NULL DEFAULT '',
                    Done      INTEGER NOT NULL DEFAULT 0,
                    Total     INTEGER NOT NULL DEFAULT 0,
                    Status    TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_mcp_job_events ON mcp_job_events(JobId, Seq);
                -- 兼容旧库：先删除重复 (JobId, Seq) 的较晚行，再建唯一索引。
                DELETE FROM mcp_job_events
                WHERE EXISTS (
                    SELECT 1 FROM mcp_job_events e2
                    WHERE e2.JobId = mcp_job_events.JobId
                      AND e2.Seq = mcp_job_events.Seq
                      AND e2.Id < mcp_job_events.Id
                );
                CREATE UNIQUE INDEX IF NOT EXISTS UX_mcp_job_events_job_seq ON mcp_job_events(JobId, Seq);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            await EnsureEventColumnsAsync(connection, ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task EnsureEventColumnsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(mcp_job_events);";
            await using var reader = await info.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                existing.Add(reader.GetString(1));
        }

        foreach (var (name, ddl) in new[]
                 {
                     ("Server", "ALTER TABLE mcp_job_events ADD COLUMN Server TEXT NOT NULL DEFAULT '';"),
                     ("Phase", "ALTER TABLE mcp_job_events ADD COLUMN Phase TEXT NOT NULL DEFAULT '';"),
                     ("Done", "ALTER TABLE mcp_job_events ADD COLUMN Done INTEGER NOT NULL DEFAULT 0;"),
                     ("Total", "ALTER TABLE mcp_job_events ADD COLUMN Total INTEGER NOT NULL DEFAULT 0;"),
                     ("Status", "ALTER TABLE mcp_job_events ADD COLUMN Status TEXT NOT NULL DEFAULT '';")
                 })
        {
            if (existing.Contains(name))
                continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = ddl;
            await alter.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// 启动清理：running 且心跳超时且进程非本进程 → orphaned（避免多进程清理误伤本进程仍存活的长任务）；
    /// 随后滚动清理过期终态。
    /// </summary>
    public async Task<int> CleanupAsync(int orphanHeartbeatSeconds, int retentionHours, int maxFinishedPerUser,
        CancellationToken ct = default, int? selfProcessId = null)
    {
        await EnsureInitializedAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-Math.Max(60, orphanHeartbeatSeconds)).ToString("O");
        var pid = selfProcessId ?? Environment.ProcessId;
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE mcp_jobs SET
                    State = 'orphaned',
                    ErrorStatus = 'orphaned',
                    ErrorText = COALESCE(ErrorText, '') || '执行进程已退出或心跳超时',
                    FinishedAt = @now
                WHERE State = 'running' AND ProcessId <> @pid
                  AND HeartbeatAt IS NOT NULL AND HeartbeatAt < @cutoff;
                """;
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@pid", pid);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // TTL：删除过期终态 job 及其事件
        var ttlCutoff = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, retentionHours)).ToString("O");
        await ExecuteAsync("""
            DELETE FROM mcp_job_events WHERE JobId IN (
                SELECT Id FROM mcp_jobs WHERE FinishedAt IS NOT NULL AND FinishedAt < @cutoff);
            DELETE FROM mcp_jobs WHERE FinishedAt IS NOT NULL AND FinishedAt < @cutoff;
            """, ("@cutoff", ttlCutoff), ct);

        // 单用户终态数量上限：按 OwnerUser 分组保留最新 maxFinishedPerUser 条
        await ExecuteAsync("""
            DELETE FROM mcp_job_events WHERE JobId IN (
                SELECT Id FROM (
                    SELECT Id, ROW_NUMBER() OVER (PARTITION BY OwnerUser ORDER BY FinishedAt DESC) AS rn
                    FROM mcp_jobs WHERE FinishedAt IS NOT NULL
                ) WHERE rn > @keep);
            DELETE FROM mcp_jobs WHERE FinishedAt IS NOT NULL AND Id IN (
                SELECT Id FROM (
                    SELECT Id, ROW_NUMBER() OVER (PARTITION BY OwnerUser ORDER BY FinishedAt DESC) AS rn
                    FROM mcp_jobs WHERE FinishedAt IS NOT NULL
                ) WHERE rn > @keep);
            """, ("@keep", Math.Max(1, maxFinishedPerUser)), ct);
        return 0;
    }

    public Task SaveAsync(JobState job, CancellationToken ct = default) =>
        ExecuteAsync("""
            INSERT INTO mcp_jobs(Id, Operation, ArgumentsJson, State, OwnerUser, SessionId, ProcessId,
                Progress, ProgressTotal, ProgressDone, ResultJson, ErrorStatus, ErrorText, Partial,
                CancelRequested, CreatedAt, HeartbeatAt, FinishedAt)
            VALUES($id, $op, $args, $state, $owner, $session, $pid,
                $progress, $ptotal, $pdone, $result, $estatus, $etext, $partial,
                $cancel, $created, $heartbeat, $finished)
            ON CONFLICT(Id) DO UPDATE SET
                State=excluded.State,
                Progress=excluded.Progress,
                ProgressTotal=excluded.ProgressTotal,
                ProgressDone=excluded.ProgressDone,
                ResultJson=excluded.ResultJson,
                ErrorStatus=excluded.ErrorStatus,
                ErrorText=excluded.ErrorText,
                Partial=excluded.Partial,
                CancelRequested=excluded.CancelRequested,
                HeartbeatAt=excluded.HeartbeatAt,
                FinishedAt=excluded.FinishedAt
            """, new (string, object?)[]
            {
                ("$id", job.Id),
                ("$op", job.Operation),
                ("$args", job.ArgumentsJson),
                ("$state", job.State),
                ("$owner", job.OwnerUser),
                ("$session", job.SessionId),
                ("$pid", job.ProcessId),
                ("$progress", job.Progress),
                ("$ptotal", job.ProgressTotal),
                ("$pdone", job.ProgressDone),
                ("$result", (object?)job.ResultJson ?? DBNull.Value),
                ("$estatus", (object?)job.ErrorStatus ?? DBNull.Value),
                ("$etext", (object?)job.ErrorText ?? DBNull.Value),
                ("$partial", job.Partial ? 1 : 0),
                ("$cancel", job.CancelRequested ? 1 : 0),
                ("$created", job.CreatedAt.ToString("O")),
                ("$heartbeat", (object?)job.HeartbeatAt?.ToString("O") ?? DBNull.Value),
                ("$finished", (object?)job.FinishedAt?.ToString("O") ?? DBNull.Value)
            }, ct);

    public async Task<JobState?> GetAsync(string jobId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM mcp_jobs WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", jobId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadJob(reader) : null;
    }

    /// <summary>非原子读（跨进程取消观察用）：仅取 State/CancelRequested。</summary>
    public async Task<(string State, bool CancelRequested)> PeekFlagsAsync(string jobId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT State, CancelRequested FROM mcp_jobs WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", jobId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return ("", false);
        return (reader.GetString(0), reader.GetInt64(1) != 0);
    }

    /// <summary>刷新心跳（事件/进度上报时调用）。</summary>
    public Task TouchHeartbeatAsync(string jobId, CancellationToken ct = default) =>
        ExecuteAsync("UPDATE mcp_jobs SET HeartbeatAt = $now WHERE Id = $id",
            new (string, object?)[] { ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", jobId) }, ct);

    /// <summary>跨进程取消标记：只更新 CancelRequested，避免覆盖执行进程并发写入的进度/状态。</summary>
    public Task SetCancelRequestedAsync(string jobId, CancellationToken ct = default) =>
        ExecuteAsync("""
            UPDATE mcp_jobs SET CancelRequested = 1
            WHERE Id = $id AND State NOT IN ('succeeded','failed','cancelled','orphaned')
            """, ("$id", jobId), ct);

    /// <summary>更新数值进度（参数化，百分比由调用方换算）。</summary>
    public Task UpdateProgressAsync(string jobId, int done, int total, double percent, CancellationToken ct = default) =>
        ExecuteAsync("UPDATE mcp_jobs SET ProgressDone = $done, ProgressTotal = $total, Progress = $pct, HeartbeatAt = $now WHERE Id = $id",
            new (string, object?)[]
            {
                ("$done", done), ("$total", total),
                ("$pct", Math.Clamp(percent, 0, 100)),
                ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", jobId)
            }, ct);

    /// <summary>标记终态（后台执行器写终态专用；仅当仍处于非终态时生效）。</summary>
    public Task MarkTerminalAsync(string jobId, string state, string? errorStatus, string? errorText, bool partial, CancellationToken ct = default) =>
        ExecuteAsync("""
            UPDATE mcp_jobs SET State = $state, ErrorStatus = $estatus, ErrorText = $etext,
                Partial = $partial, FinishedAt = $now
            WHERE Id = $id AND State NOT IN ('succeeded','failed','cancelled','orphaned')
            """,
            new (string, object?)[]
            {
                ("$state", state),
                ("$estatus", (object?)errorStatus ?? DBNull.Value),
                ("$etext", (object?)errorText ?? DBNull.Value),
                ("$partial", partial ? 1 : 0),
                ("$now", DateTimeOffset.UtcNow.ToString("O")),
                ("$id", jobId)
            }, ct);

    public async Task<IReadOnlyList<JobState>> ListAsync(string? ownerUser, string? operation, string? state,
        int limit, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var where = new List<string>();
        if (ownerUser != null) where.Add("OwnerUser = $owner");
        if (operation != null) where.Add("Operation = $op");
        if (state != null) where.Add("State = $state");
        cmd.CommandText = $"SELECT * FROM mcp_jobs" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
            " ORDER BY CreatedAt DESC LIMIT $limit";
        if (ownerUser != null) cmd.Parameters.AddWithValue("$owner", ownerUser);
        if (operation != null) cmd.Parameters.AddWithValue("$op", operation);
        if (state != null) cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<JobState>();
        while (await reader.ReadAsync(ct)) list.Add(ReadJob(reader));
        return list;
    }

    private static (string Server, string Phase, int Done, int Total, string Status) ExtractEventFields(
        string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.TrimStart()[0] != '{')
            return (string.Empty, string.Empty, 0, 0, string.Empty);
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (string.Empty, string.Empty, 0, 0, string.Empty);
            return (
                ReadString(root, "server") ?? ReadString(root, "serverId") ?? string.Empty,
                ReadString(root, "phase") ?? string.Empty,
                ReadInt(root, "done") ?? 0,
                ReadInt(root, "total") ?? 0,
                ReadString(root, "status") ?? string.Empty);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty, 0, 0, string.Empty);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    public async Task<long> AppendEventAsync(string jobId, string kind, string message, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var seqCmd = connection.CreateCommand())
        {
            seqCmd.Transaction = (SqliteTransaction)tx;
            seqCmd.CommandText = "SELECT COALESCE(MAX(Seq), 0) + 1 FROM mcp_job_events WHERE JobId = $id";
            seqCmd.Parameters.AddWithValue("$id", jobId);
            var seq = (long)(await seqCmd.ExecuteScalarAsync(ct) ?? 1L);
            var fields = ExtractEventFields(message);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO mcp_job_events(JobId, Seq, Kind, Message, Server, Phase, Done, Total, Status, CreatedAt)
                VALUES($job, $seq, $kind, $msg, $server, $phase, $done, $total, $status, $created)
                """;
            cmd.Parameters.AddWithValue("$job", jobId);
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$msg", message);
            cmd.Parameters.AddWithValue("$server", fields.Server);
            cmd.Parameters.AddWithValue("$phase", fields.Phase);
            cmd.Parameters.AddWithValue("$done", fields.Done);
            cmd.Parameters.AddWithValue("$total", fields.Total);
            cmd.Parameters.AddWithValue("$status", fields.Status);
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return seq;
        }
    }

    public async Task<IReadOnlyList<JobEvent>> GetEventsAsync(string jobId, long fromSeq, int limit, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Seq, Kind, Message, CreatedAt, Server, Phase, Done, Total, Status FROM mcp_job_events
            WHERE JobId = $id AND Seq > $from ORDER BY Seq LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.Parameters.AddWithValue("$from", fromSeq);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<JobEvent>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadEvent(reader));
        return list;
    }

    /// <summary>读取最近 N 条事件（按 Seq 升序返回），供 job_status 摘要使用。</summary>
    public async Task<IReadOnlyList<JobEvent>> GetRecentEventsAsync(string jobId, int limit, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Seq, Kind, Message, CreatedAt, Server, Phase, Done, Total, Status FROM mcp_job_events
            WHERE JobId = $id ORDER BY Seq DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<JobEvent>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadEvent(reader));
        list.Reverse();
        return list;
    }

    private static JobEvent ReadEvent(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static JobState ReadJob(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("Id")),
        Operation = reader.GetString(reader.GetOrdinal("Operation")),
        ArgumentsJson = reader.GetString(reader.GetOrdinal("ArgumentsJson")),
        State = reader.GetString(reader.GetOrdinal("State")),
        OwnerUser = reader.GetString(reader.GetOrdinal("OwnerUser")),
        SessionId = reader.GetString(reader.GetOrdinal("SessionId")),
        ProcessId = (int)reader.GetInt64(reader.GetOrdinal("ProcessId")),
        Progress = reader.GetDouble(reader.GetOrdinal("Progress")),
        ProgressTotal = (int)reader.GetInt64(reader.GetOrdinal("ProgressTotal")),
        ProgressDone = (int)reader.GetInt64(reader.GetOrdinal("ProgressDone")),
        ResultJson = reader.IsDBNull(reader.GetOrdinal("ResultJson")) ? null : reader.GetString(reader.GetOrdinal("ResultJson")),
        ErrorStatus = reader.IsDBNull(reader.GetOrdinal("ErrorStatus")) ? null : reader.GetString(reader.GetOrdinal("ErrorStatus")),
        ErrorText = reader.IsDBNull(reader.GetOrdinal("ErrorText")) ? null : reader.GetString(reader.GetOrdinal("ErrorText")),
        Partial = reader.GetInt64(reader.GetOrdinal("Partial")) != 0,
        CancelRequested = reader.GetInt64(reader.GetOrdinal("CancelRequested")) != 0,
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        HeartbeatAt = reader.IsDBNull(reader.GetOrdinal("HeartbeatAt")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("HeartbeatAt"))),
        FinishedAt = reader.IsDBNull(reader.GetOrdinal("FinishedAt")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("FinishedAt")))
    };

    private async Task ExecuteAsync(string sql, (string, object?)[] args, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecuteAsync(string sql, (string, object?) arg, CancellationToken ct) =>
        await ExecuteAsync(sql, new[] { arg }, ct);
}
