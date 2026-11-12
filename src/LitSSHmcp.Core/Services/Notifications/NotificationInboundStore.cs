using System.IO;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Core.Services.Notifications;

public sealed record NotificationInboundEvent(
    long Id,
    string Type,
    string Source,
    string Title,
    string Body,
    string Severity,
    string? DedupeKey,
    DateTime CreatedAt);

/// <summary>
/// 跨进程通知入站队列（Web/CLI/Headless 写入，App 通知中心消费）。
/// 使用与通知 Outbox 相同的 notifications.db，避免跨进程直接依赖 App 的静态服务。
/// </summary>
public sealed class NotificationInboundStore
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaReady;

    public NotificationInboundStore(string? dbPath = null) =>
        _dbPath = string.IsNullOrWhiteSpace(dbPath)
            ? Path.Combine(ConfigPaths.AppDataDir, "notifications.db")
            : dbPath;

    public async Task<long> EnqueueAsync(string type, string source, string title, string body,
        string severity = "info", string? dedupeKey = null, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO NotificationInbound(Type, Source, Title, Body, Severity, DedupeKey, CreatedAt)
            VALUES (@type, @source, @title, @body, @severity, @dedupe, @created)
            RETURNING Id;
            """;
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@body", body);
        cmd.Parameters.AddWithValue("@severity", severity);
        cmd.Parameters.AddWithValue("@dedupe", (object?)dedupeKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<List<NotificationInboundEvent>> GetPendingAsync(int limit = 100, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var result = new List<NotificationInboundEvent>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Type, Source, Title, Body, Severity, DedupeKey, CreatedAt
            FROM NotificationInbound
            WHERE ConsumedAt IS NULL
            ORDER BY Id LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new NotificationInboundEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                DateTime.TryParse(reader.GetString(7), out var created) ? created : DateTime.UtcNow));
        }
        return result;
    }

    public async Task MarkConsumedAsync(long id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE NotificationInbound SET ConsumedAt=@now WHERE Id=@id;";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=5";

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
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS NotificationInbound (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Type TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    Body TEXT NOT NULL,
                    Severity TEXT NOT NULL,
                    DedupeKey TEXT,
                    CreatedAt TEXT NOT NULL,
                    ConsumedAt TEXT
                );
                CREATE INDEX IF NOT EXISTS IX_NotificationInbound_Pending
                    ON NotificationInbound(ConsumedAt, Id);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }
}
