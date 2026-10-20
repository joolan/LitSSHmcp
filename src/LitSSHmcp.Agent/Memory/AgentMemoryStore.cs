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
    /// <summary>
    /// 向量召回；kind 非空时仅在该类别内检索，activeOnly 时排除 status=superseded 的条目。
    /// assetIds 非空时只保留「raw / 无资产归属的通用经验 / 关联资产有交集」的条目。
    /// </summary>
    Task<List<MemoryHit>> SearchAsync(float[] query, int topK, string? kind = null, bool activeOnly = true,
        IReadOnlyCollection<string>? assetIds = null, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>按 (Kind, Key) 查找结构化记忆；不存在返回 null。</summary>
    Task<MemoryRecord?> FindByKeyAsync(string kind, string key, CancellationToken ct = default);
    /// <summary>写入一条结构化记忆（新增）。</summary>
    Task<long> AddStructuredAsync(string kind, string key, string source, string content, float[] vector,
        string assetIds, string tags, int freq, CancellationToken ct = default);
    /// <summary>更新一条结构化记忆（内容/向量/资产/标签/状态；freqIncrement 可增可不增）。</summary>
    Task UpdateStructuredAsync(long id, string content, float[] vector, string assetIds, string tags,
        string status, int freqIncrement, CancellationToken ct = default);
    /// <summary>删除一条结构化记忆（按 Kind+Key）；返回是否存在。</summary>
    Task<bool> DeleteAsync(string kind, string key, CancellationToken ct = default);
    /// <summary>列出结构化记忆（kind/assetId 可选过滤，按 UpdatedAt 倒序）；includeRaw=true 时含 kind=raw 的会话/文档片段。</summary>
    Task<List<MemoryRecord>> ListAsync(string? kind, string? assetId, int limit, bool includeRaw = false, CancellationToken ct = default);
    /// <summary>按 Id 删除任意记忆（含 raw；管理界面用）。</summary>
    Task<bool> DeleteByIdAsync(long id, CancellationToken ct = default);
    /// <summary>统计记忆条数（总数、按 kind 分组、已过时条数）。</summary>
    Task<MemoryStats> GetStatsAsync(CancellationToken ct = default);
    /// <summary>读取一条记忆的向量（近重复判定用）；不存在返回 null。</summary>
    Task<float[]?> GetVectorAsync(long id, CancellationToken ct = default);
    /// <summary>同源 raw 条数超出 keep 时删除最旧的；返回删除条数。</summary>
    Task<int> PruneRawAsync(string source, int keep, CancellationToken ct = default);
}

/// <summary>记忆库统计（设置页"查看记忆统计"用）。</summary>
public sealed record MemoryStats(int Total, IReadOnlyDictionary<string, int> ByKind, int Superseded);

/// <summary>一条结构化记忆（kind=task|script|asset…，key 为语义唯一键）。</summary>
public sealed record MemoryRecord(long Id, string Source, string Content, string Kind, string Key,
    string AssetIds, string Tags, int Freq, string Status, string CreatedAt, string UpdatedAt);

public sealed record MemoryHit(string Source, string Content, double Score,
    string Kind = "raw", string Key = "", int Freq = 1, string Status = "active", string AssetIds = "");

public class AgentMemoryStore : IAgentMemoryStore
{
    private const string RawKind = "raw";

    private readonly string _dbPath;

    public AgentMemoryStore() : this(null) { }
    public AgentMemoryStore(string? dbPath) => _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.AgentDb : dbPath!;

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS agent_memory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Source TEXT NOT NULL,
    Content TEXT NOT NULL,
    Vector BLOB NOT NULL,
    CreatedAt TEXT NOT NULL,
    Kind TEXT NOT NULL DEFAULT 'raw',
    Key TEXT NOT NULL DEFAULT '',
    AssetIds TEXT NOT NULL DEFAULT '',
    Tags TEXT NOT NULL DEFAULT '',
    Freq INTEGER NOT NULL DEFAULT 1,
    UpdatedAt TEXT NOT NULL DEFAULT '',
    Status TEXT NOT NULL DEFAULT 'active'
);
CREATE INDEX IF NOT EXISTS IX_agent_memory_Source ON agent_memory(Source);
CREATE INDEX IF NOT EXISTS IX_agent_memory_KindKey ON agent_memory(Kind, Key);";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 旧库迁移：按 PRAGMA table_info 补齐缺失列（常量默认值，SQLite 允许 ADD COLUMN ... NOT NULL DEFAULT）
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(agent_memory);";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                existing.Add(reader.GetString(1));
        }
        foreach (var (column, ddl) in new[]
        {
            ("Kind", "ALTER TABLE agent_memory ADD COLUMN Kind TEXT NOT NULL DEFAULT 'raw';"),
            ("Key", "ALTER TABLE agent_memory ADD COLUMN Key TEXT NOT NULL DEFAULT '';"),
            ("AssetIds", "ALTER TABLE agent_memory ADD COLUMN AssetIds TEXT NOT NULL DEFAULT '';"),
            ("Tags", "ALTER TABLE agent_memory ADD COLUMN Tags TEXT NOT NULL DEFAULT '';"),
            ("Freq", "ALTER TABLE agent_memory ADD COLUMN Freq INTEGER NOT NULL DEFAULT 1;"),
            ("UpdatedAt", "ALTER TABLE agent_memory ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT '';"),
            ("Status", "ALTER TABLE agent_memory ADD COLUMN Status TEXT NOT NULL DEFAULT 'active';"),
        })
        {
            if (existing.Contains(column))
                continue;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_agent_memory_KindKey ON agent_memory(Kind, Key);";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task AddAsync(string source, string content, float[] vector, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO agent_memory(Source, Content, Vector, CreatedAt, Kind, Key, AssetIds, Tags, Freq, UpdatedAt, Status) " +
                          "VALUES (@s, @c, @v, @now, @k, '', '', '', 1, @now, 'active');";
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@c", content);
        cmd.Parameters.AddWithValue("@v", ToBytes(vector));
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@k", RawKind);
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

    public async Task<List<MemoryHit>> SearchAsync(float[] query, int topK, string? kind = null, bool activeOnly = true,
        IReadOnlyCollection<string>? assetIds = null, CancellationToken ct = default)
    {
        var scored = new List<MemoryHit>();
        var assets = (assetIds ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var sql = "SELECT Source, Content, Vector, Kind, Key, Freq, Status, IFNULL(AssetIds,'') FROM agent_memory WHERE 1=1";
        if (!string.IsNullOrWhiteSpace(kind))
        {
            sql += " AND Kind=@kind";
            cmd.Parameters.AddWithValue("@kind", kind);
        }
        if (activeOnly)
            sql += " AND Status='active'";
        if (assets.Length > 0)
        {
            // raw 与无资产归属的通用经验保留；结构化经验要求资产交集（逗号边界 + LIKE 转义，避免 ssh:s1 命中 ssh:s10、ssh:web_1 命中 ssh:web01）。
            var matches = new List<string>();
            for (var i = 0; i < assets.Length; i++)
            {
                matches.Add($"((',' || IFNULL(AssetIds,'') || ',') LIKE @asset{i} ESCAPE '\\')");
                cmd.Parameters.AddWithValue($"@asset{i}", $"%,{EscapeLike(assets[i])},%");
            }
            sql += $" AND (Kind='raw' OR IFNULL(AssetIds,'')='' OR {string.Join(" OR ", matches)})";
        }
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var vector = ToFloats((byte[])reader.GetValue(2));
            scored.Add(new MemoryHit(
                reader.GetString(0), reader.GetString(1), VectorMath.Cosine(query, vector),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6),
                reader.GetString(7)));
        }
        // 分数为主、频次为辅（同分时经验更多的条目优先）
        return scored
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Freq)
            .Take(Math.Max(1, topK))
            .ToList();
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_memory;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<MemoryRecord?> FindByKeyAsync(string kind, string key, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Source, Content, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt " +
                          "FROM agent_memory WHERE Kind=@k AND Key=@key LIMIT 1;";
        cmd.Parameters.AddWithValue("@k", kind);
        cmd.Parameters.AddWithValue("@key", key);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    public async Task<long> AddStructuredAsync(string kind, string key, string source, string content, float[] vector,
        string assetIds, string tags, int freq, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO agent_memory(Source, Content, Vector, CreatedAt, Kind, Key, AssetIds, Tags, Freq, UpdatedAt, Status) " +
                          "VALUES (@s, @c, @v, @now, @k, @key, @a, @t, @f, @now, 'active'); " +
                          "SELECT last_insert_rowid();";
        var now = DateTime.UtcNow.ToString("O");
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@c", content);
        cmd.Parameters.AddWithValue("@v", ToBytes(vector));
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@k", kind);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@a", assetIds);
        cmd.Parameters.AddWithValue("@t", tags);
        cmd.Parameters.AddWithValue("@f", Math.Max(1, freq));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task UpdateStructuredAsync(long id, string content, float[] vector, string assetIds, string tags,
        string status, int freqIncrement, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE agent_memory SET Content=@c, Vector=@v, AssetIds=@a, Tags=@t, Status=@st, " +
                          "Freq=Freq+@inc, UpdatedAt=@now WHERE Id=@id;";
        cmd.Parameters.AddWithValue("@c", content);
        cmd.Parameters.AddWithValue("@v", ToBytes(vector));
        cmd.Parameters.AddWithValue("@a", assetIds);
        cmd.Parameters.AddWithValue("@t", tags);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@inc", freqIncrement);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(string kind, string key, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_memory WHERE Kind=@k AND Key=@key;";
        cmd.Parameters.AddWithValue("@k", kind);
        cmd.Parameters.AddWithValue("@key", key);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteByIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_memory WHERE Id=@id;";
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<List<MemoryRecord>> ListAsync(string? kind, string? assetId, int limit, bool includeRaw = false, CancellationToken ct = default)
    {
        var result = new List<MemoryRecord>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var sql = "SELECT Id, Source, Content, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt " +
                  "FROM agent_memory WHERE 1=1";
        if (!includeRaw)
            sql += " AND Kind<>'raw'";
        if (!string.IsNullOrWhiteSpace(kind))
        {
            sql += " AND Kind=@kind";
            cmd.Parameters.AddWithValue("@kind", kind);
        }
        if (!string.IsNullOrWhiteSpace(assetId))
        {
            sql += " AND AssetIds LIKE @asset";
            cmd.Parameters.AddWithValue("@asset", "%" + assetId + "%");
        }
        sql += " ORDER BY UpdatedAt DESC, Id DESC LIMIT @limit;";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadRecord(reader));
        return result;
    }

    public async Task<MemoryStats> GetStatsAsync(CancellationToken ct = default)
    {
        var empty = new MemoryStats(0, new Dictionary<string, int>(), 0);
        if (!File.Exists(_dbPath))
            return empty;

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_memory';";
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) == 0)
                return empty;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT IFNULL(Kind,''), IFNULL(Status,''), COUNT(*) FROM agent_memory GROUP BY Kind, Status;";
        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var superseded = 0;
        var total = 0L;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(0);
            var status = reader.GetString(1);
            var count = reader.GetInt32(2);
            total += count;
            byKind.TryGetValue(kind, out var prev);
            byKind[kind] = prev + count;
            if (status == "superseded")
                superseded += count;
        }
        return new MemoryStats((int)Math.Min(int.MaxValue, total), byKind, superseded);
    }

    public async Task<float[]?> GetVectorAsync(long id, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Vector FROM agent_memory WHERE Id=@id;";
        cmd.Parameters.AddWithValue("@id", id);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is byte[] bytes ? ToFloats(bytes) : null;
    }

    public async Task<int> PruneRawAsync(string source, int keep, CancellationToken ct = default)
    {
        if (keep < 0)
            return 0;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        // 保留最近 keep 条（按 Id 倒序），其余同源 raw 删除
        cmd.CommandText = "DELETE FROM agent_memory WHERE Source=@s AND Kind='raw' AND Id NOT IN " +
                          "(SELECT Id FROM agent_memory WHERE Source=@s AND Kind='raw' ORDER BY Id DESC LIMIT @keep);";
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@keep", keep);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static MemoryRecord ReadRecord(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetString(8),
        reader.GetString(9), reader.GetString(10));

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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
