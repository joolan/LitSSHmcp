using System.IO;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.App.Services.Notifications;

public sealed record NotificationDelivery(
    long Id,
    long MessageId,
    string MessageType,
    string Source,
    string Title,
    string Body,
    NotificationSeverity Severity,
    string? DedupeKey,
    string ChannelId,
    string ChannelJson,
    string Status,
    int Attempts,
    DateTime NextAttemptAt,
    string? LastError,
    DateTime CreatedAt,
    DateTime? SentAt);

/// <summary>通知投递 Outbox：Webhook/外部渠道先入队，后台重试；失败超过上限进入 dead。</summary>
public sealed class NotificationOutboxStore
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaReady;

    public NotificationOutboxStore(string? dbPath = null) =>
        _dbPath = string.IsNullOrWhiteSpace(dbPath)
            ? Path.Combine(ConfigPaths.AppDataDir, "notifications.db")
            : dbPath;

    public async Task<long> EnqueueAsync(NotificationMessage message, NotificationChannelConfig channel,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO NotificationOutbox
                (MessageType, Source, Title, Body, Severity, DedupeKey, ChannelId, ChannelJson,
                 Status, Attempts, NextAttemptAt, CreatedAt)
            VALUES (@type, @source, @title, @body, @severity, @dedupe, @channelId, @channelJson,
                    'pending', 0, @next, @created)
            RETURNING Id;
            """;
        cmd.Parameters.AddWithValue("@type", message.Type);
        cmd.Parameters.AddWithValue("@source", message.Source);
        cmd.Parameters.AddWithValue("@title", message.Title);
        cmd.Parameters.AddWithValue("@body", message.Message);
        cmd.Parameters.AddWithValue("@severity", (int)message.Severity);
        cmd.Parameters.AddWithValue("@dedupe", (object?)message.DedupeKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@channelId", channel.Id);
        cmd.Parameters.AddWithValue("@channelJson", JsonSerializer.Serialize(Sanitize(channel)));
        cmd.Parameters.AddWithValue("@next", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<List<NotificationDelivery>> GetPendingAsync(int limit = 20, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var result = new List<NotificationDelivery>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, MessageType, Source, Title, Body, Severity, DedupeKey, ChannelId, ChannelJson,
                   Status, Attempts, NextAttemptAt, LastError, CreatedAt, SentAt
            FROM NotificationOutbox
            WHERE Status = 'pending' AND NextAttemptAt <= @now
            ORDER BY Id LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(Read(reader));
        return result;
    }

    public async Task<List<NotificationDelivery>> ListRecentAsync(int limit = 200, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var result = new List<NotificationDelivery>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, MessageType, Source, Title, Body, Severity, DedupeKey, ChannelId, ChannelJson,
                   Status, Attempts, NextAttemptAt, LastError, CreatedAt, SentAt
            FROM NotificationOutbox
            ORDER BY Id DESC LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(Read(reader));
        return result;
    }

    public async Task RetryAsync(long id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE NotificationOutbox
            SET Status='pending', Attempts=0, NextAttemptAt=@now, LastError=NULL
            WHERE Id=@id AND Status IN ('pending','dead');
            """;
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task MarkSentAsync(long id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE NotificationOutbox SET Status='sent', SentAt=@now, LastError=NULL WHERE Id=@id;";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(long id, int attempts, string error, DateTime nextAttemptAt,
        bool dead, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE NotificationOutbox
            SET Status=@status, Attempts=@attempts, NextAttemptAt=@next, LastError=@error
            WHERE Id=@id;
            """;
        cmd.Parameters.AddWithValue("@status", dead ? "dead" : "pending");
        cmd.Parameters.AddWithValue("@attempts", attempts);
        cmd.Parameters.AddWithValue("@next", nextAttemptAt.ToString("O"));
        cmd.Parameters.AddWithValue("@error", error.Length > 1000 ? error[..1000] : error);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PruneAsync(int retentionDays = 30, int keepPerChannel = 1000,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM NotificationOutbox
            WHERE (Status IN ('sent','dead') AND CreatedAt < @cutoff)
               OR Id IN (
                    SELECT Id FROM (
                        SELECT Id, ROW_NUMBER() OVER (PARTITION BY ChannelId ORDER BY Id DESC) AS rn
                        FROM NotificationOutbox
                    ) WHERE rn > @keep
               );
            """;
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays)).ToString("O"));
        cmd.Parameters.AddWithValue("@keep", Math.Max(1, keepPerChannel));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static NotificationChannelConfig Sanitize(NotificationChannelConfig channel) => new()
    {
        Id = channel.Id,
        Name = channel.Name,
        Type = channel.Type,
        Enabled = channel.Enabled,
        Url = channel.Url,
        Secret = null,
        Template = channel.Template,
        TimeoutSeconds = channel.TimeoutSeconds,
        Headers = new Dictionary<string, string>(channel.Headers ?? new(), StringComparer.OrdinalIgnoreCase)
    };

    private static NotificationDelivery Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        (NotificationSeverity)reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetInt32(10),
        DateTime.TryParse(reader.GetString(11), out var next) ? next : DateTime.UtcNow,
        reader.IsDBNull(12) ? null : reader.GetString(12),
        DateTime.TryParse(reader.GetString(13), out var created) ? created : DateTime.UtcNow,
        reader.IsDBNull(14) ? null : DateTime.TryParse(reader.GetString(14), out var sent) ? sent : null);

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
                CREATE TABLE IF NOT EXISTS NotificationOutbox (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    MessageType TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    Body TEXT NOT NULL,
                    Severity INTEGER NOT NULL,
                    DedupeKey TEXT,
                    ChannelId TEXT NOT NULL,
                    ChannelJson TEXT NOT NULL,
                    Status TEXT NOT NULL DEFAULT 'pending',
                    Attempts INTEGER NOT NULL DEFAULT 0,
                    NextAttemptAt TEXT NOT NULL,
                    LastError TEXT,
                    CreatedAt TEXT NOT NULL,
                    SentAt TEXT
                );
                CREATE INDEX IF NOT EXISTS IX_NotificationOutbox_Pending
                    ON NotificationOutbox(Status, NextAttemptAt);
                CREATE INDEX IF NOT EXISTS IX_NotificationOutbox_Channel
                    ON NotificationOutbox(ChannelId, Id DESC);
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
