using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Agent;

/// <summary>AI 助手会话与消息的持久化（独立库 agent.db）。</summary>
public interface IContextStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<long> CreateSessionAsync(string title, CancellationToken ct = default);
    Task<List<AgentSessionRow>> ListSessionsAsync(CancellationToken ct = default);
    Task RenameSessionAsync(long id, string title, CancellationToken ct = default);
    Task DeleteSessionAsync(long id, CancellationToken ct = default);

    /// <summary>记录该会话最近一次使用的模型 Id（切换会话时优先用它）。</summary>
    Task SetSessionProviderAsync(long id, string providerId, CancellationToken ct = default);

    Task AppendMessageAsync(long sessionId, string role, string content, CancellationToken ct = default);

    /// <summary>用给定的完整消息序列替换该会话的持久化记录（编辑/重发/重生成后保持与界面一致）。</summary>
    Task ReplaceMessagesAsync(long sessionId, IEnumerable<(string Role, string Content)> messages, CancellationToken ct = default);

    Task<List<AgentMessageRow>> GetMessagesAsync(long sessionId, CancellationToken ct = default);
    Task TouchSessionAsync(long id, CancellationToken ct = default);

    /// <summary>按保留策略裁剪历史（每会话消息上限 / 最旧会话天数 / 会话数上限）；返回删除的消息数。</summary>
    Task<int> PruneAsync(int maxSessions, int maxMessagesPerSession, int retentionDays, CancellationToken ct = default);
}

public sealed record AgentSessionRow(long Id, string Title, string CreatedAt, string UpdatedAt, string? ProviderId = null);
public sealed record AgentMessageRow(string Role, string Content, string Timestamp);

public class ContextStore : IContextStore
{
    private readonly string _dbPath;

    public ContextStore() : this(null) { }
    public ContextStore(string? dbPath) => _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.AgentDb : dbPath!;

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS agent_sessions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Title TEXT NOT NULL DEFAULT '',
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS agent_messages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId INTEGER NOT NULL,
    Role TEXT NOT NULL,
    Content TEXT NOT NULL,
    Timestamp TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_agent_messages_SessionId ON agent_messages(SessionId, Id);";
        await cmd.ExecuteNonQueryAsync(ct);

        // 兼容旧库：补 ProviderId 列（已存在则忽略）
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN ProviderId TEXT;";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }
    }

    public async Task<long> CreateSessionAsync(string title, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var now = DateTime.UtcNow.ToString("O");
        cmd.CommandText = "INSERT INTO agent_sessions(Title, CreatedAt, UpdatedAt) VALUES (@t, @now, @now); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@t", title ?? string.Empty);
        cmd.Parameters.AddWithValue("@now", now);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<List<AgentSessionRow>> ListSessionsAsync(CancellationToken ct = default)
    {
        var list = new List<AgentSessionRow>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, CreatedAt, UpdatedAt, ProviderId FROM agent_sessions ORDER BY UpdatedAt DESC;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new AgentSessionRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        return list;
    }

    public Task RenameSessionAsync(long id, string title, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET Title=@v, UpdatedAt=@now WHERE Id=@id;",
            new() { ["@v"] = title, ["@now"] = DateTime.UtcNow.ToString("O"), ["@id"] = id }, ct);

    public Task SetSessionProviderAsync(long id, string providerId, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET ProviderId=@v WHERE Id=@id;",
            new() { ["@v"] = providerId ?? string.Empty, ["@id"] = id }, ct);

    public Task DeleteSessionAsync(long id, CancellationToken ct = default) =>
        ExecAsync("DELETE FROM agent_messages WHERE SessionId=@id; DELETE FROM agent_sessions WHERE Id=@id;",
            new() { ["@id"] = id }, ct);

    public Task TouchSessionAsync(long id, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET UpdatedAt=@now WHERE Id=@id;",
            new() { ["@now"] = DateTime.UtcNow.ToString("O"), ["@id"] = id }, ct);

    public Task AppendMessageAsync(long sessionId, string role, string content, CancellationToken ct = default) =>
        ExecAsync("INSERT INTO agent_messages(SessionId, Role, Content, Timestamp) VALUES (@s, @r, @c, @now);",
            new() { ["@s"] = sessionId, ["@r"] = role, ["@c"] = content, ["@now"] = DateTime.UtcNow.ToString("O") }, ct);

    public async Task ReplaceMessagesAsync(long sessionId, IEnumerable<(string Role, string Content)> messages, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM agent_messages WHERE SessionId=@s;";
            del.Parameters.AddWithValue("@s", sessionId);
            await del.ExecuteNonQueryAsync(ct);
        }

        foreach (var (role, content) in messages)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO agent_messages(SessionId, Role, Content, Timestamp) VALUES (@s, @r, @c, @now);";
            insert.Parameters.AddWithValue("@s", sessionId);
            insert.Parameters.AddWithValue("@r", role);
            insert.Parameters.AddWithValue("@c", content);
            insert.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<List<AgentMessageRow>> GetMessagesAsync(long sessionId, CancellationToken ct = default)
    {
        var list = new List<AgentMessageRow>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Role, Content, Timestamp FROM agent_messages WHERE SessionId=@s ORDER BY Id;";
        cmd.Parameters.AddWithValue("@s", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new AgentMessageRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    public async Task<int> PruneAsync(int maxSessions, int maxMessagesPerSession, int retentionDays, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        async Task<int> RunAsync(string sql, Dictionary<string, object> args)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args)
                cmd.Parameters.AddWithValue(k, v);
            return await cmd.ExecuteNonQueryAsync(ct);
        }

        var deleted = 0;

        // 1) 每会话消息上限（保留最新 N 条）
        if (maxMessagesPerSession > 0)
        {
            deleted += await RunAsync(
                @"DELETE FROM agent_messages WHERE Id IN (
                    SELECT Id FROM (SELECT Id, ROW_NUMBER() OVER (PARTITION BY SessionId ORDER BY Id DESC) AS rn FROM agent_messages)
                    WHERE rn > @n);",
                new() { ["@n"] = maxMessagesPerSession });
        }

        // 2) 过期会话（按天数）及其消息
        if (retentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("O");
            await RunAsync("DELETE FROM agent_messages WHERE SessionId IN (SELECT Id FROM agent_sessions WHERE UpdatedAt < @cutoff);",
                new() { ["@cutoff"] = cutoff });
            await RunAsync("DELETE FROM agent_sessions WHERE UpdatedAt < @cutoff;", new() { ["@cutoff"] = cutoff });
        }

        // 3) 会话数上限（保留最新 N 个）
        if (maxSessions > 0)
        {
            await RunAsync(
                "DELETE FROM agent_messages WHERE SessionId NOT IN (SELECT Id FROM (SELECT Id FROM agent_sessions ORDER BY UpdatedAt DESC LIMIT @n));",
                new() { ["@n"] = maxSessions });
            await RunAsync(
                "DELETE FROM agent_sessions WHERE Id NOT IN (SELECT Id FROM (SELECT Id FROM agent_sessions ORDER BY UpdatedAt DESC LIMIT @n));",
                new() { ["@n"] = maxSessions });
        }

        return deleted;
    }

    private async Task ExecAsync(string sql, Dictionary<string, object> args, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
