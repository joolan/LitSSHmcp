using System.Globalization;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Agent.Scheduling;

/// <summary>
/// AI 定时任务与运行历史存储（独立 SQLite 库 ai-tasks.db）。
/// 同时承担跨进程租约：同一任务同一时刻只允许一个宿主执行。
/// </summary>
public sealed class AiTaskStore
{
    private const string TaskColumns =
        "Id, Name, Instruction, Enabled, ScheduleType, IntervalMinutes, WeekDays, MonthDays, TimeOfDay, OnceAt, " +
        "ProviderId, AgentId, ReadOnly, MaxTurns, TimeoutSeconds, TokenBudget, NotifyApp, NotifyOnlyOnIssue, " +
        "EmailGroupIds, WebhookIds, RunOnStartupIfMissed, Source, OwnerUserId, NextRunAt, LastRunAt, LastResult, " +
        "LastSessionId, RunRequestedAt, LeaseOwner, LeaseUntil, CreatedAt, UpdatedAt";

    private readonly string _dbPath;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaReady;

    /// <summary>进程级默认实例（桌面/工具共用）。测试可传入独立路径。</summary>
    public static AiTaskStore Default { get; } = new();

    public AiTaskStore(string? dbPath = null)
        => _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.AiTaskDb : dbPath!;

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=5";

    /// <summary>建表并把上次进程崩溃遗留的 Running 运行改判为 Failed。进程启动时调用一次。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE AiTaskRuns
SET Status = 'Failed', CompletedAt = @Now, Error = COALESCE(Error, 'interrupted_by_restart')
WHERE Status = 'Running';";
        cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaReady)
            return;

        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady)
                return;

            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(ct);

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                await pragma.ExecuteNonQueryAsync(ct);
            }

            await using (var create = connection.CreateCommand())
            {
                create.CommandText = @"
CREATE TABLE IF NOT EXISTS AiTasks (
    Id                  TEXT PRIMARY KEY,
    Name                TEXT NOT NULL,
    Instruction         TEXT NOT NULL,
    Enabled             INTEGER NOT NULL DEFAULT 1,
    ScheduleType        TEXT NOT NULL,
    IntervalMinutes     INTEGER NOT NULL DEFAULT 60,
    WeekDays            TEXT NOT NULL DEFAULT '[]',
    MonthDays           TEXT NOT NULL DEFAULT '[]',
    TimeOfDay           TEXT NOT NULL DEFAULT '08:00',
    OnceAt              TEXT,
    ProviderId          TEXT,
    AgentId             TEXT,
    ReadOnly            INTEGER NOT NULL DEFAULT 1,
    MaxTurns            INTEGER NOT NULL DEFAULT 0,
    TimeoutSeconds      INTEGER NOT NULL DEFAULT 1800,
    TokenBudget         INTEGER NOT NULL DEFAULT 0,
    NotifyApp           INTEGER NOT NULL DEFAULT 1,
    NotifyOnlyOnIssue   INTEGER NOT NULL DEFAULT 1,
    EmailGroupIds       TEXT NOT NULL DEFAULT '[]',
    WebhookIds          TEXT NOT NULL DEFAULT '[]',
    RunOnStartupIfMissed INTEGER NOT NULL DEFAULT 1,
    Source              TEXT NOT NULL DEFAULT 'User',
    OwnerUserId         TEXT NOT NULL DEFAULT 'local',
    NextRunAt           TEXT,
    LastRunAt           TEXT,
    LastResult          TEXT,
    LastSessionId       INTEGER,
    RunRequestedAt      TEXT,
    LeaseOwner          TEXT,
    LeaseUntil          TEXT,
    CreatedAt           TEXT NOT NULL,
    UpdatedAt           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_AiTasks_NextRun ON AiTasks(Enabled, NextRunAt);

CREATE TABLE IF NOT EXISTS AiTaskRuns (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    TaskId          TEXT NOT NULL,
    SessionId       INTEGER,
    TriggerType     TEXT NOT NULL,
    StartedAt       TEXT NOT NULL,
    CompletedAt     TEXT,
    Status          TEXT NOT NULL,
    Error           TEXT,
    Summary         TEXT,
    InputTokens     INTEGER NOT NULL DEFAULT 0,
    OutputTokens    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_AiTaskRuns_Task ON AiTaskRuns(TaskId, StartedAt DESC);";
                await create.ExecuteNonQueryAsync(ct);
            }

            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    public async Task<List<AiTaskConfig>> ListAsync(bool includeDisabled = true, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var result = new List<AiTaskConfig>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {TaskColumns} FROM AiTasks" +
                          (includeDisabled ? string.Empty : " WHERE Enabled = 1") +
                          " ORDER BY CreatedAt ASC;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadTask(reader));
        return result;
    }

    public async Task<AiTaskConfig?> GetAsync(string id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {TaskColumns} FROM AiTasks WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTask(reader) : null;
    }

    public async Task<AiTaskConfig?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var tasks = await ListAsync(true, ct);
        return tasks.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task CreateAsync(AiTaskConfig task, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var now = DateTime.Now;
        task.CreatedAt = task.CreatedAt == default ? now : task.CreatedAt;
        task.UpdatedAt = now;
        task.NextRunAt ??= AiTaskSchedule.ComputeNextRun(task, now);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO AiTasks (
    Id, Name, Instruction, Enabled, ScheduleType, IntervalMinutes, WeekDays, MonthDays, TimeOfDay, OnceAt,
    ProviderId, AgentId, ReadOnly, MaxTurns, TimeoutSeconds, TokenBudget, NotifyApp, NotifyOnlyOnIssue,
    EmailGroupIds, WebhookIds, RunOnStartupIfMissed, Source, OwnerUserId, NextRunAt, LastRunAt, LastResult,
    LastSessionId, RunRequestedAt, LeaseOwner, LeaseUntil, CreatedAt, UpdatedAt)
VALUES (
    @Id, @Name, @Instruction, @Enabled, @ScheduleType, @IntervalMinutes, @WeekDays, @MonthDays, @TimeOfDay, @OnceAt,
    @ProviderId, @AgentId, @ReadOnly, @MaxTurns, @TimeoutSeconds, @TokenBudget, @NotifyApp, @NotifyOnlyOnIssue,
    @EmailGroupIds, @WebhookIds, @RunOnStartupIfMissed, @Source, @OwnerUserId, @NextRunAt, @LastRunAt, @LastResult,
    @LastSessionId, @RunRequestedAt, @LeaseOwner, @LeaseUntil, @CreatedAt, @UpdatedAt);";
        AddTaskParameters(cmd, task);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(AiTaskConfig task, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        task.UpdatedAt = DateTime.Now;
        task.NextRunAt ??= AiTaskSchedule.ComputeNextRun(task, DateTime.Now);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE AiTasks SET
    Name = @Name, Instruction = @Instruction, Enabled = @Enabled, ScheduleType = @ScheduleType,
    IntervalMinutes = @IntervalMinutes, WeekDays = @WeekDays, MonthDays = @MonthDays, TimeOfDay = @TimeOfDay,
    OnceAt = @OnceAt, ProviderId = @ProviderId, AgentId = @AgentId, ReadOnly = @ReadOnly, MaxTurns = @MaxTurns,
    TimeoutSeconds = @TimeoutSeconds, TokenBudget = @TokenBudget, NotifyApp = @NotifyApp,
    NotifyOnlyOnIssue = @NotifyOnlyOnIssue, EmailGroupIds = @EmailGroupIds, WebhookIds = @WebhookIds,
    RunOnStartupIfMissed = @RunOnStartupIfMissed, Source = @Source, OwnerUserId = @OwnerUserId,
    NextRunAt = @NextRunAt, LastRunAt = @LastRunAt, LastResult = @LastResult, LastSessionId = @LastSessionId,
    RunRequestedAt = @RunRequestedAt, UpdatedAt = @UpdatedAt
WHERE Id = @Id;";
        AddTaskParameters(cmd, task);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var task = await GetAsync(id, ct);
        if (task is null)
            return;
        task.Enabled = enabled;
        task.NextRunAt = enabled ? AiTaskSchedule.ComputeNextRun(task, DateTime.Now) : null;
        await UpdateAsync(task, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using (var runs = connection.CreateCommand())
        {
            runs.Transaction = tx;
            runs.CommandText = "DELETE FROM AiTaskRuns WHERE TaskId = @Id;";
            runs.Parameters.AddWithValue("@Id", id);
            await runs.ExecuteNonQueryAsync(ct);
        }
        await using (var task = connection.CreateCommand())
        {
            task.Transaction = tx;
            task.CommandText = "DELETE FROM AiTasks WHERE Id = @Id;";
            task.Parameters.AddWithValue("@Id", id);
            await task.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>请求立即运行：由调度器在下一轮领取。</summary>
    public async Task RequestRunAsync(string id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE AiTasks SET RunRequestedAt = @Now, UpdatedAt = @Now WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>抢占租约；成功返回 true。租约用于多进程（App/Web/headless）互斥。</summary>
    public async Task<bool> TryAcquireLeaseAsync(string id, string owner, TimeSpan duration, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var now = DateTime.UtcNow;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE AiTasks SET LeaseOwner = @Owner, LeaseUntil = @Until, UpdatedAt = @Now
WHERE Id = @Id AND Enabled = 1
  AND (LeaseUntil IS NULL OR LeaseUntil < @Now);";
        cmd.Parameters.AddWithValue("@Owner", owner);
        cmd.Parameters.AddWithValue("@Until", now.Add(duration).ToString("O"));
        cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task ReleaseLeaseAsync(string id, string owner, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE AiTasks SET LeaseOwner = NULL, LeaseUntil = NULL, UpdatedAt = @Now
WHERE Id = @Id AND LeaseOwner = @Owner;";
        cmd.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Owner", owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> BeginRunAsync(string taskId, AiTaskTrigger trigger, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO AiTaskRuns (TaskId, TriggerType, StartedAt, Status)
VALUES (@TaskId, @Trigger, @StartedAt, 'Running');
SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@TaskId", taskId);
        cmd.Parameters.AddWithValue("@Trigger", trigger.ToString());
        cmd.Parameters.AddWithValue("@StartedAt", DateTime.Now.ToString("O"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task CompleteRunAsync(long runId, AiTaskRunStatus status, long? sessionId,
        string? summary, string? error, long inputTokens, long outputTokens, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE AiTaskRuns SET
    Status = @Status, SessionId = @SessionId, Summary = @Summary, Error = @Error,
    InputTokens = @InputTokens, OutputTokens = @OutputTokens, CompletedAt = @CompletedAt
WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Status", status.ToString());
        cmd.Parameters.AddWithValue("@SessionId", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Summary", (object?)Truncate(summary, 8000) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Error", (object?)Truncate(error, 4000) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@InputTokens", inputTokens);
        cmd.Parameters.AddWithValue("@OutputTokens", outputTokens);
        cmd.Parameters.AddWithValue("@CompletedAt", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", runId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>运行结束后更新任务的下次运行、上次结果与会话，并清理手动运行标记。</summary>
    public async Task UpdateAfterRunAsync(string id, DateTime? nextRunAt, string? lastResult,
        long? sessionId, bool disable, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE AiTasks SET
    NextRunAt = @NextRunAt, LastRunAt = @LastRunAt, LastResult = @LastResult, LastSessionId = @LastSessionId,
    RunRequestedAt = NULL, Enabled = CASE WHEN @Disable = 1 THEN 0 ELSE Enabled END, UpdatedAt = @Now
WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@NextRunAt", (object?)ToText(nextRunAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastRunAt", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("@LastResult", (object?)Truncate(lastResult, 2000) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastSessionId", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Disable", disable ? 1 : 0);
        cmd.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<AiTaskRun>> ListRunsAsync(string taskId, int limit = 20, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var result = new List<AiTaskRun>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT Id, TaskId, SessionId, TriggerType, StartedAt, CompletedAt, Status, Error, Summary, InputTokens, OutputTokens
FROM AiTaskRuns WHERE TaskId = @TaskId ORDER BY Id DESC LIMIT @Limit;";
        cmd.Parameters.AddWithValue("@TaskId", taskId);
        cmd.Parameters.AddWithValue("@Limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new AiTaskRun
            {
                Id = reader.GetInt64(0),
                TaskId = reader.GetString(1),
                SessionId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                Trigger = ParseEnum(reader.GetString(3), AiTaskTrigger.Schedule),
                StartedAt = ParseDate(reader.GetString(4)) ?? DateTime.MinValue,
                CompletedAt = reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5)),
                Status = ParseEnum(reader.GetString(6), AiTaskRunStatus.Failed),
                Error = reader.IsDBNull(7) ? null : reader.GetString(7),
                Summary = reader.IsDBNull(8) ? null : reader.GetString(8),
                InputTokens = reader.GetInt64(9),
                OutputTokens = reader.GetInt64(10)
            });
        }
        return result;
    }

    public async Task<int> PruneRunsAsync(string taskId, int keep, CancellationToken ct = default)
    {
        if (keep <= 0)
            return 0;
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
DELETE FROM AiTaskRuns WHERE TaskId = @TaskId AND Id NOT IN (
    SELECT Id FROM AiTaskRuns WHERE TaskId = @TaskId ORDER BY Id DESC LIMIT @Keep);";
        cmd.Parameters.AddWithValue("@TaskId", taskId);
        cmd.Parameters.AddWithValue("@Keep", keep);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddTaskParameters(SqliteCommand cmd, AiTaskConfig task)
    {
        cmd.Parameters.AddWithValue("@Id", task.Id);
        cmd.Parameters.AddWithValue("@Name", task.Name);
        cmd.Parameters.AddWithValue("@Instruction", task.Instruction);
        cmd.Parameters.AddWithValue("@Enabled", task.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@ScheduleType", task.ScheduleType.ToString());
        cmd.Parameters.AddWithValue("@IntervalMinutes", task.IntervalMinutes);
        cmd.Parameters.AddWithValue("@WeekDays", JsonSerializer.Serialize(task.WeekDays ?? Array.Empty<int>()));
        cmd.Parameters.AddWithValue("@MonthDays", JsonSerializer.Serialize(task.MonthDays ?? Array.Empty<int>()));
        cmd.Parameters.AddWithValue("@TimeOfDay", task.TimeOfDay);
        cmd.Parameters.AddWithValue("@OnceAt", (object?)ToText(task.OnceAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ProviderId", (object?)task.ProviderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AgentId", (object?)task.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ReadOnly", task.ReadOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("@MaxTurns", task.MaxTurns);
        cmd.Parameters.AddWithValue("@TimeoutSeconds", task.TimeoutSeconds);
        cmd.Parameters.AddWithValue("@TokenBudget", task.TokenBudget);
        cmd.Parameters.AddWithValue("@NotifyApp", task.NotifyApp ? 1 : 0);
        cmd.Parameters.AddWithValue("@NotifyOnlyOnIssue", task.NotifyOnlyOnIssue ? 1 : 0);
        cmd.Parameters.AddWithValue("@EmailGroupIds", JsonSerializer.Serialize(task.EmailGroupIds ?? Array.Empty<string>()));
        cmd.Parameters.AddWithValue("@WebhookIds", JsonSerializer.Serialize(task.WebhookIds ?? Array.Empty<string>()));
        cmd.Parameters.AddWithValue("@RunOnStartupIfMissed", task.RunOnStartupIfMissed ? 1 : 0);
        cmd.Parameters.AddWithValue("@Source", task.Source);
        cmd.Parameters.AddWithValue("@OwnerUserId", task.OwnerUserId);
        cmd.Parameters.AddWithValue("@NextRunAt", (object?)ToText(task.NextRunAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastRunAt", (object?)ToText(task.LastRunAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastResult", (object?)task.LastResult ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastSessionId", (object?)task.LastSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@RunRequestedAt", (object?)ToText(task.RunRequestedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LeaseOwner", (object?)task.LeaseOwner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LeaseUntil", (object?)ToText(task.LeaseUntil) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", task.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@UpdatedAt", task.UpdatedAt.ToString("O"));
    }

    private static AiTaskConfig ReadTask(SqliteDataReader reader)
    {
        string? Str(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
        int Int(string name) => reader.GetInt32(reader.GetOrdinal(name));
        long Long(string name) => reader.GetInt64(reader.GetOrdinal(name));
        bool Bool(string name) => reader.GetInt64(reader.GetOrdinal(name)) != 0;
        DateTime? Date(string name) => ParseDate(Str(name));
        int[] IntArray(string name) => ParseIntArray(Str(name));

        return new AiTaskConfig
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Instruction = reader.GetString(reader.GetOrdinal("Instruction")),
            Enabled = Bool("Enabled"),
            ScheduleType = ParseEnum(Str("ScheduleType"), AiTaskScheduleType.Daily),
            IntervalMinutes = Int("IntervalMinutes"),
            WeekDays = IntArray("WeekDays"),
            MonthDays = IntArray("MonthDays"),
            TimeOfDay = Str("TimeOfDay") ?? "08:00",
            OnceAt = Date("OnceAt"),
            ProviderId = Str("ProviderId"),
            AgentId = Str("AgentId"),
            ReadOnly = Bool("ReadOnly"),
            MaxTurns = Int("MaxTurns"),
            TimeoutSeconds = Int("TimeoutSeconds"),
            TokenBudget = Int("TokenBudget"),
            NotifyApp = Bool("NotifyApp"),
            NotifyOnlyOnIssue = Bool("NotifyOnlyOnIssue"),
            EmailGroupIds = ParseStringArray(Str("EmailGroupIds")),
            WebhookIds = ParseStringArray(Str("WebhookIds")),
            RunOnStartupIfMissed = Bool("RunOnStartupIfMissed"),
            Source = Str("Source") ?? "User",
            OwnerUserId = Str("OwnerUserId") ?? "local",
            NextRunAt = Date("NextRunAt"),
            LastRunAt = Date("LastRunAt"),
            LastResult = Str("LastResult"),
            LastSessionId = reader.IsDBNull(reader.GetOrdinal("LastSessionId"))
                ? null : Long("LastSessionId"),
            RunRequestedAt = Date("RunRequestedAt"),
            LeaseOwner = Str("LeaseOwner"),
            LeaseUntil = Date("LeaseUntil"),
            CreatedAt = Date("CreatedAt") ?? DateTime.Now,
            UpdatedAt = Date("UpdatedAt") ?? DateTime.Now
        };
    }

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;
    }

    private static string? ToText(DateTime? value) => value?.ToString("O");

    private static int[] ParseIntArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<int>();
        try
        {
            return JsonSerializer.Deserialize<int[]>(json) ?? Array.Empty<int>();
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    private static string[] ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is { Length: > 0 } ? (value.Length <= max ? value : value[..max] + "...") : value;
}
