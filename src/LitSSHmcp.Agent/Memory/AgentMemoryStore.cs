using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Agent;

/// <summary>本地向量记忆存储（agent.db 的 agent_memory 表）。</summary>
public interface IAgentMemoryStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task AddAsync(string source, string content, float[] vector, CancellationToken ct = default);
    Task<bool> ExistsAsync(string source, string content, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<List<MemoryHit>> SearchAsync(float[] query, int topK, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public sealed record MemoryHit(string Source, string Content, double Score);

public class AgentMemoryStore : IAgentMemoryStore
{
    private readonly string _dbPath;

    public AgentMemoryStore() : this(null) { }
    public AgentMemoryStore(string? dbPath) => _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.AgentDb : dbPath!;

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS agent_memory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Source TEXT NOT NULL,
    Content TEXT NOT NULL,
    Vector BLOB NOT NULL,
    CreatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_agent_memory_Source ON agent_memory(Source);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddAsync(string source, string content, float[] vector, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO agent_memory(Source, Content, Vector, CreatedAt) VALUES (@s, @c, @v, @now);";
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@c", content);
        cmd.Parameters.AddWithValue("@v", ToBytes(vector));
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ExistsAsync(string source, string content, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM agent_memory WHERE Source=@s AND Content=@c LIMIT 1;";
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@c", content);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM agent_memory;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<List<MemoryHit>> SearchAsync(float[] query, int topK, CancellationToken ct = default)
    {
        var scored = new List<MemoryHit>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Source, Content, Vector FROM agent_memory;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var vector = ToFloats((byte[])reader.GetValue(2));
            scored.Add(new MemoryHit(reader.GetString(0), reader.GetString(1), VectorMath.Cosine(query, vector)));
        }
        return scored.OrderByDescending(h => h.Score).Take(Math.Max(1, topK)).ToList();
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_memory;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] ToFloats(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }
}
