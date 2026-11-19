using System.IO;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.App.Services.DatasourceAi;

public sealed record DatasourceAiSessionInfo(
    long Id,
    string Title,
    string DatasourceId,
    string DatasourceKind,
    string Database,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public string Subtitle => $"{DatasourceKind} · {Database}";
    public string TimeText => UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");
}

public sealed record DatasourceAiStoredMessage(
    long Id,
    string Role,
    string Content,
    string? Tool,
    string? AuditKey,
    DateTime CreatedAt);

/// <summary>
/// 数据源 AI 会话与消息，保存在 agent.db。
/// SQL 执行记录仍写 SqlAuditLog，通过 dsai:&lt;sessionId&gt; + workbench_ai 关联。
/// </summary>
public sealed class DatasourceAiStore
{
    private const string SessionTable = "datasource_ai_sessions";
    private const string MessageTable = "datasource_ai_messages";
    private readonly string _dbPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public DatasourceAiStore(string? dbPath = null)
        => _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.AgentDb : dbPath;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await using var connection = new SqliteConnection($"Data Source={_dbPath};Default Timeout=5");
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {SessionTable} (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL DEFAULT '',
                    DatasourceId TEXT NOT NULL DEFAULT '',
                    DatasourceKind TEXT NOT NULL DEFAULT '',
                    Database TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_{SessionTable}_Scope
                    ON {SessionTable}(DatasourceId, DatasourceKind, Database, UpdatedAt DESC);
                CREATE TABLE IF NOT EXISTS {MessageTable} (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SessionId INTEGER NOT NULL,
                    Role TEXT NOT NULL,
                    Content TEXT NOT NULL DEFAULT '',
                    Tool TEXT NULL,
                    AuditKey TEXT NULL,
                    CreatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_{MessageTable}_Session
                    ON {MessageTable}(SessionId, Id);
                """;
            await command.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DatasourceAiSessionInfo?> GetLatestSessionAsync(
        string datasourceId, string datasourceKind, string database, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection($"Data Source={_dbPath};Default Timeout=5");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Title, DatasourceId, DatasourceKind, Database, CreatedAt, UpdatedAt
            FROM {SessionTable}
            WHERE DatasourceId=@ds AND DatasourceKind=@kind AND Database=@db
            ORDER BY UpdatedAt DESC, Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@ds", datasourceId);
        command.Parameters.AddWithValue("@kind", datasourceKind);
        command.Parameters.AddWithValue("@db", database);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return ReadSession(reader);
    }

    public async Task<IReadOnlyList<DatasourceAiSessionInfo>> ListSessionsAsync(
        string datasourceId, string datasourceKind, string database, int limit = 30, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection($"Data Source={_dbPath};Default Timeout=5");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Title, DatasourceId, DatasourceKind, Database, CreatedAt, UpdatedAt
            FROM {SessionTable}
            WHERE DatasourceId=@ds AND DatasourceKind=@kind AND Database=@db
            ORDER BY UpdatedAt DESC, Id DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@ds", datasourceId);
        command.Parameters.AddWithValue("@kind", datasourceKind);
        command.Parameters.AddWithValue("@db", database);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
        var result = new List<DatasourceAiSessionInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadSession(reader));
        return result;
    }

    public async Task<long> CreateSessionAsync(
        string datasourceId, string datasourceKind, string database, string title, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection($"Data Source={_dbPath};Default Timeout=5");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var now = DateTime.UtcNow.ToString("O");
        command.CommandText = $"""
            INSERT INTO {SessionTable}(Title, DatasourceId, DatasourceKind, Database, CreatedAt, UpdatedAt)
            VALUES (@title, @ds, @kind, @db, @now, @now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@title", Trim(title, 120));
        command.Parameters.AddWithValue("@ds", datasourceId);
        command.Parameters.AddWithValue("@kind", datasourceKind);
        command.Parameters.AddWithValue("@db", database);
        command.Parameters.AddWithValue("@now", now);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<DatasourceAiStoredMessage>> LoadMessagesAsync(
        long sessionId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection($"Data Source={_dbPath};Default Timeout=5");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Role, Content, Tool, AuditKey, CreatedAt
            FROM {MessageTable}
            WHERE SessionId=@sid
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("@sid", sessionId);
        var result = new List<DatasourceAiStoredMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new DatasourceAiStoredMessage(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseTime(reader.GetString(5))));
        }
        return result;
    }

    public async Task<long> AppendMessageAsync(
        long sessionId, string role, string content, string? tool = null,
        string? auditKey = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection($"Data Source={_dbPath};Default Timeout=5");
        await connection.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var now = DateTime.UtcNow.ToString("O");
        command.CommandText = $"""
            INSERT INTO {MessageTable}(SessionId, Role, Content, Tool, AuditKey, CreatedAt)
            VALUES (@sid, @role, @content, @tool, @audit, @now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@sid", sessionId);
        command.Parameters.AddWithValue("@role", role);
        command.Parameters.AddWithValue("@content", content ?? string.Empty);
        command.Parameters.AddWithValue("@tool", (object?)tool ?? DBNull.Value);
        command.Parameters.AddWithValue("@audit", (object?)auditKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", now);
        var id = (long)(await command.ExecuteScalarAsync(ct))!;

        await using var touch = connection.CreateCommand();
        touch.Transaction = transaction;
        touch.CommandText = $"UPDATE {SessionTable} SET UpdatedAt=@now WHERE Id=@sid;";
        touch.Parameters.AddWithValue("@now", now);
        touch.Parameters.AddWithValue("@sid", sessionId);
        await touch.ExecuteNonQueryAsync(ct);
        transaction.Commit();
        return id;
    }

    public async Task DeleteSessionAsync(long sessionId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection($"Data Source={_dbPath};Default Timeout=5");
        await connection.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();
        await using var deleteMessages = connection.CreateCommand();
        deleteMessages.Transaction = transaction;
        deleteMessages.CommandText = $"DELETE FROM {MessageTable} WHERE SessionId=@sid;";
        deleteMessages.Parameters.AddWithValue("@sid", sessionId);
        await deleteMessages.ExecuteNonQueryAsync(ct);

        await using var deleteSession = connection.CreateCommand();
        deleteSession.Transaction = transaction;
        deleteSession.CommandText = $"DELETE FROM {SessionTable} WHERE Id=@sid;";
        deleteSession.Parameters.AddWithValue("@sid", sessionId);
        await deleteSession.ExecuteNonQueryAsync(ct);
        transaction.Commit();
    }

    public async Task RenameSessionAsync(long sessionId, string title, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection($"Data Source={_dbPath};Default Timeout=5");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {SessionTable} SET Title=@title, UpdatedAt=@now WHERE Id=@sid;";
        command.Parameters.AddWithValue("@title", Trim(title, 120));
        command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("@sid", sessionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static DatasourceAiSessionInfo ReadSession(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseTime(reader.GetString(5)),
            ParseTime(reader.GetString(6)));

    private static DateTime ParseTime(string text)
        => DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value
            : DateTime.UtcNow;

    private static string Trim(string? text, int max)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max];
    }
}
