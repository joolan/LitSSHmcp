using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Agent;

/// <summary>
/// 记忆访问范围（Web 强制隔离）：普通成员读团队 + 自己私有、写自己私有；
/// 管理员读团队 + 自己私有、写团队。null = 桌面/管理界面，保持全量行为。
/// </summary>
public sealed record MemoryAccessScope(string OwnerUserId, bool IsAdmin)
{
    /// <summary>写入作用域：管理员写团队，普通成员写私有。</summary>
    public string WriteScope => IsAdmin ? "team" : "private";
}

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
    /// <param name="writableOnly">true 时仅返回当前用户可修改的条目（近重复合并用，避免成员改写团队记忆）。</param>
    Task<List<MemoryRecord>> ListAsync(string? kind, string? assetId, int limit, bool includeRaw = false,
        CancellationToken ct = default, bool writableOnly = false);
    /// <summary>按 Id 删除任意记忆（含 raw；管理界面用）。</summary>
    Task<bool> DeleteByIdAsync(long id, CancellationToken ct = default);
    /// <summary>统计记忆条数（总数、按 kind 分组、已过时条数）。</summary>
    Task<MemoryStats> GetStatsAsync(CancellationToken ct = default);
    /// <summary>读取一条记忆的向量（近重复判定用）；不存在返回 null。</summary>
    Task<float[]?> GetVectorAsync(long id, CancellationToken ct = default);
    /// <summary>同源 raw 条数超出 keep 时删除最旧的；返回删除条数。</summary>
    Task<int> PruneRawAsync(string source, int keep, CancellationToken ct = default);

    /// <summary>列出指定记忆的历史快照（每次更新/删除前归档一版，Version 倒序）。</summary>
    Task<List<MemoryVersion>> ListVersionsAsync(long memoryId, CancellationToken ct = default);
    /// <summary>把历史快照恢复为当前版本（先归档当前状态，版本号继续递增）；返回是否成功。</summary>
    Task<bool> RestoreVersionAsync(long memoryId, int version, CancellationToken ct = default);
    /// <summary>归档一条记忆：状态置为 superseded 并保留历史（可回滚）；返回是否存在。</summary>
    Task<bool> ArchiveByIdAsync(long id, CancellationToken ct = default);
}

/// <summary>记忆库统计（设置页"查看记忆统计"用）。</summary>
public sealed record MemoryStats(int Total, IReadOnlyDictionary<string, int> ByKind, int Superseded);

/// <summary>一条结构化记忆（kind=task|script|asset…，key 为语义唯一键）。</summary>
public sealed record MemoryRecord(long Id, string Source, string Content, string Kind, string Key,
    string AssetIds, string Tags, int Freq, string Status, string CreatedAt, string UpdatedAt);

/// <summary>一条记忆的历史快照（更新/删除/归档前的状态，用于审计与回滚）。</summary>
public sealed record MemoryVersion(long MemoryId, int Version, string Source, string Content, string Kind,
    string Key, string AssetIds, string Tags, int Freq, string Status, string CreatedAt, string UpdatedAt,
    string OwnerUserId, string Scope, string ArchivedAt);

public sealed record MemoryHit(string Source, string Content, double Score,
    string Kind = "raw", string Key = "", int Freq = 1, string Status = "active", string AssetIds = "");

public class AgentMemoryStore : IAgentMemoryStore
{
    private const string RawKind = "raw";

    private readonly string _dbPath;
    private readonly MemoryAccessScope? _access;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialized;

    public AgentMemoryStore() : this(null) { }
    public AgentMemoryStore(string? dbPath, MemoryAccessScope? access = null)
    {
        if (access is not null && string.IsNullOrWhiteSpace(access.OwnerUserId))
            throw new ArgumentException("隔离记忆必须提供 OwnerUserId", nameof(access));
        _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.AgentDb : dbPath!;
        _access = access;
    }

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    /// <summary>可见范围：团队记忆 + 当前用户私有记忆；不限定时返回 null。</summary>
    private string? AddVisibleFilter(SqliteCommand cmd)
    {
        if (_access is null)
            return null;
        cmd.Parameters.AddWithValue("@scopeOwner", _access.OwnerUserId);
        return "(Scope='team' OR (Scope='private' AND OwnerUserId=@scopeOwner))";
    }

    /// <summary>可写范围：管理员可写团队 + 自己的私有；普通成员只能写自己的私有。</summary>
    private string? AddWritableFilter(SqliteCommand cmd)
    {
        if (_access is null)
            return null;
        cmd.Parameters.AddWithValue("@scopeOwner", _access.OwnerUserId);
        return _access.IsAdmin
            ? "(Scope='team' OR (Scope='private' AND OwnerUserId=@scopeOwner))"
            : "(Scope='private' AND OwnerUserId=@scopeOwner)";
    }

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
    Status TEXT NOT NULL DEFAULT 'active',
    OwnerUserId TEXT NOT NULL DEFAULT '',
    Scope TEXT NOT NULL DEFAULT 'team',
    Version INTEGER NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS IX_agent_memory_Source ON agent_memory(Source);
CREATE TABLE IF NOT EXISTS agent_memory_history (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    MemoryId INTEGER NOT NULL,
    Version INTEGER NOT NULL,
    Source TEXT NOT NULL,
    Content TEXT NOT NULL,
    Vector BLOB NOT NULL,
    Kind TEXT NOT NULL,
    Key TEXT NOT NULL,
    AssetIds TEXT NOT NULL DEFAULT '',
    Tags TEXT NOT NULL DEFAULT '',
    Freq INTEGER NOT NULL DEFAULT 1,
    Status TEXT NOT NULL DEFAULT 'active',
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    OwnerUserId TEXT NOT NULL DEFAULT '',
    Scope TEXT NOT NULL DEFAULT 'team',
    ArchivedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_agent_memory_history_Memory ON agent_memory_history(MemoryId, Version);";
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
            ("OwnerUserId", "ALTER TABLE agent_memory ADD COLUMN OwnerUserId TEXT NOT NULL DEFAULT '';"),
            ("Scope", "ALTER TABLE agent_memory ADD COLUMN Scope TEXT NOT NULL DEFAULT 'team';"),
            ("Version", "ALTER TABLE agent_memory ADD COLUMN Version INTEGER NOT NULL DEFAULT 1;"),
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
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_agent_memory_KindKey ON agent_memory(Kind, Key); " +
                              "CREATE INDEX IF NOT EXISTS IX_agent_memory_OwnerScope ON agent_memory(OwnerUserId, Scope);";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        _initialized = true;
    }

    /// <summary>写入/删除/历史接口的懒初始化：旧库在首次使用前没有历史表。</summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;
        await _initGate.WaitAsync(ct);
        try
        {
            if (!_initialized)
                await InitializeAsync(ct);
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task AddAsync(string source, string content, float[] vector, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO agent_memory(Source, Content, Vector, CreatedAt, Kind, Key, AssetIds, Tags, Freq, UpdatedAt, Status, OwnerUserId, Scope, Version) " +
                          "VALUES (@s, @c, @v, @now, @k, '', '', '', 1, @now, 'active', @owner, @scope, 1);";
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@c", content);
        cmd.Parameters.AddWithValue("@v", ToBytes(vector));
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@k", RawKind);
        cmd.Parameters.AddWithValue("@owner", _access?.OwnerUserId ?? string.Empty);
        cmd.Parameters.AddWithValue("@scope", _access is null ? "team" : "private");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ExistsAsync(string source, string content, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var scope = AddVisibleFilter(cmd);
        cmd.CommandText = "SELECT 1 FROM agent_memory WHERE Source=@s AND Content=@c" +
                          (scope is null ? string.Empty : " AND " + scope) + " LIMIT 1;";
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@c", content);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var scope = AddVisibleFilter(cmd);
        cmd.CommandText = "SELECT COUNT(*) FROM agent_memory" + (scope is null ? string.Empty : " WHERE " + scope) + ";";
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
        var visible = AddVisibleFilter(cmd);
        if (visible is not null)
            sql += " AND " + visible;
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
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        // 清空用于更换 Embeddings 模型/维度：历史向量同样失效，一并清理，避免回滚出不可用向量。
        if (_access is null)
        {
            cmd.CommandText = "DELETE FROM agent_memory_history; DELETE FROM agent_memory;";
        }
        else
        {
            cmd.CommandText = "DELETE FROM agent_memory_history WHERE Scope='private' AND OwnerUserId=@scopeOwner; " +
                              "DELETE FROM agent_memory WHERE Scope='private' AND OwnerUserId=@scopeOwner;";
            cmd.Parameters.AddWithValue("@scopeOwner", _access.OwnerUserId);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<MemoryRecord?> FindByKeyAsync(string kind, string key, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var writable = AddWritableFilter(cmd);
        cmd.CommandText = "SELECT Id, Source, Content, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt " +
                          "FROM agent_memory WHERE Kind=@k AND Key=@key" +
                          (writable is null ? string.Empty : " AND " + writable) +
                          " ORDER BY CASE Scope WHEN 'team' THEN 0 ELSE 1 END LIMIT 1;";
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
        cmd.CommandText = "INSERT INTO agent_memory(Source, Content, Vector, CreatedAt, Kind, Key, AssetIds, Tags, Freq, UpdatedAt, Status, OwnerUserId, Scope, Version) " +
                          "VALUES (@s, @c, @v, @now, @k, @key, @a, @t, @f, @now, 'active', @owner, @scope, 1); " +
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
        cmd.Parameters.AddWithValue("@owner", _access?.OwnerUserId ?? string.Empty);
        cmd.Parameters.AddWithValue("@scope", _access?.WriteScope ?? "team");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task UpdateStructuredAsync(long id, string content, float[] vector, string assetIds, string tags,
        string status, int freqIncrement, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var writable = AddWritableFilter(cmd);
        var scopeFilter = writable is null ? string.Empty : " AND " + writable;
        // 更新前把当前版本写入历史，保证任何一次覆盖都可回滚。
        cmd.CommandText =
            "INSERT INTO agent_memory_history(MemoryId, Version, Source, Content, Vector, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, ArchivedAt) " +
            "SELECT Id, Version, Source, Content, Vector, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, @now " +
            "FROM agent_memory WHERE Id=@id" + scopeFilter + "; " +
            "UPDATE agent_memory SET Content=@c, Vector=@v, AssetIds=@a, Tags=@t, Status=@st, " +
            "Freq=Freq+@inc, UpdatedAt=@now, Version=Version+1 WHERE Id=@id" + scopeFilter + ";";
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
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var writable = AddWritableFilter(cmd);
        var scopeFilter = writable is null ? string.Empty : " AND " + writable;
        // 删除前归档当前版本；历史保留，后续可由管理员恢复到指定版本。
        cmd.CommandText =
            "INSERT INTO agent_memory_history(MemoryId, Version, Source, Content, Vector, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, ArchivedAt) " +
            "SELECT Id, Version, Source, Content, Vector, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, @now " +
            "FROM agent_memory WHERE Kind=@k AND Key=@key" + scopeFilter + "; " +
            "DELETE FROM agent_memory WHERE Kind=@k AND Key=@key" + scopeFilter + ";";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@k", kind);
        cmd.Parameters.AddWithValue("@key", key);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteByIdAsync(long id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var writable = AddWritableFilter(cmd);
        var scopeFilter = writable is null ? string.Empty : " AND " + writable;
        cmd.CommandText =
            "INSERT INTO agent_memory_history(MemoryId, Version, Source, Content, Vector, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, ArchivedAt) " +
            "SELECT Id, Version, Source, Content, Vector, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, @now " +
            "FROM agent_memory WHERE Id=@id" + scopeFilter + "; " +
            "DELETE FROM agent_memory WHERE Id=@id" + scopeFilter + ";";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<List<MemoryRecord>> ListAsync(string? kind, string? assetId, int limit, bool includeRaw = false,
        CancellationToken ct = default, bool writableOnly = false)
    {
        var result = new List<MemoryRecord>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var sql = "SELECT Id, Source, Content, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt " +
                  "FROM agent_memory WHERE 1=1";
        if (!includeRaw)
            sql += " AND Kind<>'raw'";
        var scope = writableOnly ? AddWritableFilter(cmd) : AddVisibleFilter(cmd);
        if (scope is not null)
            sql += " AND " + scope;
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
        var scope = AddVisibleFilter(cmd);
        cmd.CommandText = "SELECT IFNULL(Kind,''), IFNULL(Status,''), COUNT(*) FROM agent_memory" +
                          (scope is null ? string.Empty : " WHERE " + scope) + " GROUP BY Kind, Status;";
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
        // 保留最近 keep 条（按 Id 倒序），其余同源 raw 删除；隔离模式下只清理当前用户的私有 raw
        var owned = _access is null
            ? string.Empty
            : " AND Scope='private' AND OwnerUserId=@scopeOwner";
        if (_access is not null)
            cmd.Parameters.AddWithValue("@scopeOwner", _access.OwnerUserId);
        cmd.CommandText = "DELETE FROM agent_memory WHERE Source=@s AND Kind='raw'" + owned + " AND Id NOT IN " +
                          "(SELECT Id FROM agent_memory WHERE Source=@s AND Kind='raw'" + owned +
                          " ORDER BY Id DESC LIMIT @keep);";
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@keep", keep);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<MemoryVersion>> ListVersionsAsync(long memoryId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var result = new List<MemoryVersion>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var visible = AddVisibleFilter(cmd);
        cmd.CommandText =
            "SELECT MemoryId, Version, Source, Content, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, ArchivedAt " +
            "FROM agent_memory_history WHERE MemoryId=@id" +
            (visible is null ? string.Empty : " AND " + visible) +
            " ORDER BY Version DESC, Id DESC;";
        cmd.Parameters.AddWithValue("@id", memoryId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadVersion(reader));
        return result;
    }

    public async Task<bool> RestoreVersionAsync(long memoryId, int version, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        var snapshot = await ReadVersionAsync(connection, memoryId, version, ct);
        if (snapshot is null || !CanWriteSnapshot(snapshot))
            return false;

        var vector = await ReadVersionVectorAsync(connection, memoryId, version, ct) ?? Array.Empty<float>();
        var now = DateTime.UtcNow.ToString("O");

        // 回滚本身也可撤销：先把当前版本归档，再写入历史快照（版本号继续递增）。
        await using (var snapshotCmd = connection.CreateCommand())
        {
            snapshotCmd.CommandText =
                "INSERT INTO agent_memory_history(MemoryId, Version, Source, Content, Vector, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, ArchivedAt) " +
                "SELECT Id, Version, Source, Content, Vector, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, @now " +
                "FROM agent_memory WHERE Id=@id;";
            snapshotCmd.Parameters.AddWithValue("@now", now);
            snapshotCmd.Parameters.AddWithValue("@id", memoryId);
            await snapshotCmd.ExecuteNonQueryAsync(ct);
        }

        var exists = false;
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM agent_memory WHERE Id=@id;";
            check.Parameters.AddWithValue("@id", memoryId);
            exists = Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0;
        }

        int next;
        await using (var max = connection.CreateCommand())
        {
            max.CommandText = "SELECT IFNULL(MAX(Version),0)+1 FROM agent_memory_history WHERE MemoryId=@id;";
            max.Parameters.AddWithValue("@id", memoryId);
            next = Convert.ToInt32(await max.ExecuteScalarAsync(ct));
        }

        await using var write = connection.CreateCommand();
        write.Parameters.AddWithValue("@s", snapshot.Source);
        write.Parameters.AddWithValue("@c", snapshot.Content);
        write.Parameters.AddWithValue("@v", ToBytes(vector));
        write.Parameters.AddWithValue("@k", snapshot.Kind);
        write.Parameters.AddWithValue("@key", snapshot.Key);
        write.Parameters.AddWithValue("@a", snapshot.AssetIds);
        write.Parameters.AddWithValue("@t", snapshot.Tags);
        write.Parameters.AddWithValue("@f", snapshot.Freq);
        write.Parameters.AddWithValue("@st", snapshot.Status);
        write.Parameters.AddWithValue("@created", snapshot.CreatedAt);
        write.Parameters.AddWithValue("@now", now);
        write.Parameters.AddWithValue("@owner", snapshot.OwnerUserId);
        write.Parameters.AddWithValue("@scope", snapshot.Scope);
        write.Parameters.AddWithValue("@id", memoryId);
        write.Parameters.AddWithValue("@next", next);
        write.CommandText = exists
            ? "UPDATE agent_memory SET Source=@s, Content=@c, Vector=@v, Kind=@k, Key=@key, AssetIds=@a, Tags=@t, " +
              "Freq=@f, Status=@st, UpdatedAt=@now, Version=@next WHERE Id=@id;"
            : "INSERT INTO agent_memory(Id, Source, Content, Vector, CreatedAt, Kind, Key, AssetIds, Tags, Freq, UpdatedAt, Status, OwnerUserId, Scope, Version) " +
              "VALUES (@id, @s, @c, @v, @created, @k, @key, @a, @t, @f, @now, @st, @owner, @scope, @next);";
        return await write.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> ArchiveByIdAsync(long id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var writable = AddWritableFilter(cmd);
        var scopeFilter = writable is null ? string.Empty : " AND " + writable;
        var now = DateTime.UtcNow.ToString("O");
        cmd.CommandText =
            "INSERT INTO agent_memory_history(MemoryId, Version, Source, Content, Vector, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, ArchivedAt) " +
            "SELECT Id, Version, Source, Content, Vector, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, @now " +
            "FROM agent_memory WHERE Id=@id" + scopeFilter + "; " +
            "UPDATE agent_memory SET Status='superseded', Version=Version+1, UpdatedAt=@now WHERE Id=@id" + scopeFilter + ";";
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<MemoryVersion?> ReadVersionAsync(SqliteConnection connection, long memoryId, int version,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        var visible = AddVisibleFilter(cmd);
        cmd.CommandText =
            "SELECT MemoryId, Version, Source, Content, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt, OwnerUserId, Scope, ArchivedAt " +
            "FROM agent_memory_history WHERE MemoryId=@id AND Version=@version" +
            (visible is null ? string.Empty : " AND " + visible) +
            " ORDER BY Id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", memoryId);
        cmd.Parameters.AddWithValue("@version", version);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    private static async Task<float[]?> ReadVersionVectorAsync(SqliteConnection connection, long memoryId, int version,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Vector FROM agent_memory_history WHERE MemoryId=@id AND Version=@version ORDER BY Id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", memoryId);
        cmd.Parameters.AddWithValue("@version", version);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is byte[] bytes ? ToFloats(bytes) : null;
    }

    private bool CanWriteSnapshot(MemoryVersion snapshot)
    {
        if (_access is null)
            return true;
        if (string.Equals(snapshot.Scope, "team", StringComparison.OrdinalIgnoreCase))
            return _access.IsAdmin;
        return string.Equals(snapshot.Scope, "private", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(snapshot.OwnerUserId, _access.OwnerUserId, StringComparison.Ordinal);
    }

    private static MemoryVersion ReadVersion(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8), reader.GetString(9),
        reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetString(13), reader.GetString(14));

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
