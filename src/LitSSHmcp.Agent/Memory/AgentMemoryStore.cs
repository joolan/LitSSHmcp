using System.Security.Cryptography;
using System.Text;
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
        IReadOnlyCollection<string>? assetIds = null, bool includeRaw = true, int maxScanRows = 5000,
        CancellationToken ct = default, string? queryText = null);
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

    /// <summary>文档在记忆库中的稳定文档键（含团队/私有作用域；同一文件不同用户互不覆盖）。</summary>
    string CreateDocumentKey(string fullPath);

    /// <summary>记录一次事件/事故（不可变主记录 + 资产关系）；返回事件 Id。</summary>
    Task<long> AddIncidentAsync(string title, string symptom, string assetIds, string rootCause, string actions,
        string outcome, string evidence, double confidence, string status, string sessionId,
        CancellationToken ct = default);
    /// <summary>读取一条事件（按可见作用域）；不存在返回 null。</summary>
    Task<IncidentRecord?> GetIncidentAsync(long id, CancellationToken ct = default);
    /// <summary>列出事件（可按资产/状态过滤，按 UpdatedAt 倒序）。</summary>
    Task<List<IncidentRecord>> ListIncidentsAsync(string? assetId, string? status, int limit,
        CancellationToken ct = default);
    /// <summary>把旧事件标记为 superseded 并指向新事件；返回是否更新成功。</summary>
    Task<bool> SupersedeIncidentAsync(long oldId, long newId, CancellationToken ct = default);
    /// <summary>读取某个记忆/事件关联的资产 Id（用于校验 memory_asset 关系）。</summary>
    Task<List<string>> ListMemoryAssetIdsAsync(string ownerKind, long ownerId, CancellationToken ct = default);
    /// <summary>按资产精确查询结构化记忆（走 memory_asset 关系，避免 LIKE 前缀误命中）。</summary>
    Task<List<MemoryRecord>> ListByAssetAsync(string assetId, string? kind, int limit, CancellationToken ct = default);

    /// <summary>读取尚未 consolidation 的 raw 会话片段（按 Id 升序，最多 limit 条）。</summary>
    Task<List<RawMemoryRecord>> ListUnconsolidatedRawAsync(int limit, CancellationToken ct = default);
    /// <summary>标记 raw 片段已 consolidation（避免重复提炼）。</summary>
    Task<int> MarkRawConsolidatedAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);
    /// <summary>追加一条 consolidation 候选；同作用域 DedupeKey 已存在返回 null。</summary>
    Task<long?> AddConsolidationCandidateAsync(string kind, string payloadJson, string assetIds, string evidence,
        double confidence, string status, string sourceRefs, string dedupeKey, CancellationToken ct = default);
    /// <summary>按 Id 读取 consolidation 候选（可见作用域）；不存在返回 null。</summary>
    Task<ConsolidationCandidate?> GetConsolidationCandidateAsync(long id, CancellationToken ct = default);
    /// <summary>按状态列出 consolidation 候选（可见作用域，按时间倒序）。</summary>
    Task<List<ConsolidationCandidate>> ListConsolidationCandidatesAsync(string? status, int limit,
        CancellationToken ct = default);
    /// <summary>更新候选状态/应用结果/拒绝原因；返回是否更新成功。</summary>
    Task<bool> UpdateConsolidationCandidateAsync(long id, string status, string appliedRef, string rejectReason,
        CancellationToken ct = default);
    /// <summary>开始一次 consolidation run（用于审计/限额/观测）。</summary>
    Task<long> StartConsolidationRunAsync(CancellationToken ct = default);
    /// <summary>结束 consolidation run。</summary>
    Task FinishConsolidationRunAsync(long id, string status, int rawCount, int candidateCount, int appliedCount,
        string error, CancellationToken ct = default);
    /// <summary>统计指定时间后的 visible consolidation 运行次数与处理 raw 数（用于每日配额）。</summary>
    Task<(int Runs, int RawCount)> GetConsolidationUsageSinceAsync(DateTimeOffset since,
        CancellationToken ct = default);
    /// <summary>列出近期的 consolidation 运行记录（供运营/Web 面板查看）。</summary>
    Task<List<ConsolidationRunRecord>> ListConsolidationRunsAsync(int limit, CancellationToken ct = default);
    /// <summary>文档内容 hash + embeddings 模型未变时返回 true，可跳过重新索引。</summary>
    Task<bool> IsDocumentCurrentAsync(string docKey, string hash, string model, CancellationToken ct = default);
    /// <summary>原子替换一份文档的所有 chunk，并记录 hash/模型/分块数（旧 chunk 与旧版单段索引一并清理）。</summary>
    Task ReplaceDocumentChunksAsync(string docKey, string root, string legacySource, string hash, string model,
        IReadOnlyList<string> sources, IReadOnlyList<string> contents, IReadOnlyList<float[]> vectors,
        CancellationToken ct = default);

    /// <summary>列出指定记忆的历史快照（每次更新/删除前归档一版，Version 倒序）。</summary>
    Task<List<MemoryVersion>> ListVersionsAsync(long memoryId, CancellationToken ct = default);
    /// <summary>按记忆裁剪历史快照（默认每个 MemoryId 仅保留最近 <paramref name="keep"/> 个）；返回删除条数。</summary>
    Task<int> PruneHistoryAsync(int keep, long? memoryId = null, CancellationToken ct = default);
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
    string Kind = "raw", string Key = "", int Freq = 1, string Status = "active", string AssetIds = "",
    string CreatedAt = "", string UpdatedAt = "", long Id = 0);

/// <summary>一次运维事件/事故的结构化记录（现象→证据→根因→处置→结果）。</summary>
public sealed record IncidentRecord(long Id, string Title, string Symptom, string AssetIds,
    string RootCause, string Actions, string Outcome, string Evidence, double Confidence,
    string Status, string SessionId, string CreatedAt, string UpdatedAt, long? SupersededBy);

/// <summary>一条尚未 consolidation 的 raw 片段。</summary>
public sealed record RawMemoryRecord(long Id, string Source, string Content, string CreatedAt);

/// <summary>一条 consolidation 候选（incident/asset_fact/task/script/skill）。</summary>
public sealed record ConsolidationCandidate(long Id, string Kind, string PayloadJson, string AssetIds,
    string Evidence, double Confidence, string Status, string SourceRefs, string CreatedAt,
    string UpdatedAt, string AppliedRef, string RejectReason);

public sealed record ConsolidationRunRecord(long Id, string StartedAt, string FinishedAt, string Status,
    int RawCount, int CandidateCount, int AppliedCount, string Error, string OwnerUserId, string Scope);

public class AgentMemoryStore : IAgentMemoryStore
{
    private const string RawKind = "raw";

    /// <summary>每个 MemoryId 默认保留的历史版本数（自动清理；防止长期运行无限膨胀）。</summary>
    public const int DefaultHistoryKeep = 40;

    /// <summary>已删除记忆的历史快照保留天数（过期由全局清理删除，避免孤儿历史累积）。</summary>
    public const int OrphanHistoryRetentionDays = 30;

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
CREATE INDEX IF NOT EXISTS IX_agent_memory_history_Memory ON agent_memory_history(MemoryId, Version);
CREATE TABLE IF NOT EXISTS agent_doc_state (
    DocKey TEXT PRIMARY KEY,
    Root TEXT NOT NULL DEFAULT '',
    Hash TEXT NOT NULL,
    Model TEXT NOT NULL DEFAULT '',
    ChunkCount INTEGER NOT NULL DEFAULT 0,
    UpdatedAt TEXT NOT NULL,
    OwnerUserId TEXT NOT NULL DEFAULT '',
    Scope TEXT NOT NULL DEFAULT 'team'
);
CREATE INDEX IF NOT EXISTS IX_agent_doc_state_Root ON agent_doc_state(Root);
CREATE TABLE IF NOT EXISTS agent_incident (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Title TEXT NOT NULL,
    Symptom TEXT NOT NULL DEFAULT '',
    AssetIds TEXT NOT NULL DEFAULT '',
    RootCause TEXT NOT NULL DEFAULT '',
    Actions TEXT NOT NULL DEFAULT '',
    Outcome TEXT NOT NULL DEFAULT '',
    Evidence TEXT NOT NULL DEFAULT '',
    Confidence REAL NOT NULL DEFAULT 0.5,
    Status TEXT NOT NULL DEFAULT 'resolved',
    SessionId TEXT NOT NULL DEFAULT '',
    OwnerUserId TEXT NOT NULL DEFAULT '',
    Scope TEXT NOT NULL DEFAULT 'team',
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    SupersededBy INTEGER
);
CREATE INDEX IF NOT EXISTS IX_agent_incident_Visible ON agent_incident(Scope, OwnerUserId, Status);
CREATE INDEX IF NOT EXISTS IX_agent_incident_UpdatedAt ON agent_incident(UpdatedAt);
CREATE TABLE IF NOT EXISTS memory_asset (
    OwnerKind TEXT NOT NULL,
    OwnerId INTEGER NOT NULL,
    AssetId TEXT NOT NULL,
    PRIMARY KEY (OwnerKind, OwnerId, AssetId)
);
CREATE INDEX IF NOT EXISTS IX_memory_asset_Asset ON memory_asset(AssetId);
CREATE TABLE IF NOT EXISTS agent_consolidation_candidate (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Kind TEXT NOT NULL,
    PayloadJson TEXT NOT NULL,
    AssetIds TEXT NOT NULL DEFAULT '',
    Evidence TEXT NOT NULL DEFAULT '',
    Confidence REAL NOT NULL DEFAULT 0,
    Status TEXT NOT NULL DEFAULT 'pending',
    SourceRefs TEXT NOT NULL DEFAULT '',
    DedupeKey TEXT NOT NULL,
    OwnerUserId TEXT NOT NULL DEFAULT '',
    Scope TEXT NOT NULL DEFAULT 'team',
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    AppliedRef TEXT NOT NULL DEFAULT '',
    RejectReason TEXT NOT NULL DEFAULT ''
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_agent_consolidation_candidate_Dedupe
    ON agent_consolidation_candidate(DedupeKey, Scope, OwnerUserId);
CREATE INDEX IF NOT EXISTS IX_agent_consolidation_candidate_Status
    ON agent_consolidation_candidate(Status, CreatedAt);
CREATE TABLE IF NOT EXISTS agent_consolidation_run (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StartedAt TEXT NOT NULL,
    FinishedAt TEXT NOT NULL DEFAULT '',
    Status TEXT NOT NULL DEFAULT 'running',
    RawCount INTEGER NOT NULL DEFAULT 0,
    CandidateCount INTEGER NOT NULL DEFAULT 0,
    AppliedCount INTEGER NOT NULL DEFAULT 0,
    Error TEXT NOT NULL DEFAULT '',
    OwnerUserId TEXT NOT NULL DEFAULT '',
    Scope TEXT NOT NULL DEFAULT 'team'
);";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await InitializeFtsAsync(connection, ct);

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
            ("ConsolidatedAt", "ALTER TABLE agent_memory ADD COLUMN ConsolidatedAt TEXT NOT NULL DEFAULT '';"),
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

    /// <summary>FTS5 词语索引（外部内容表 + 触发器）；不可用时自动退回纯向量检索。</summary>
    private async Task InitializeFtsAsync(SqliteConnection connection, CancellationToken ct)
    {
        try
        {
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE VIRTUAL TABLE IF NOT EXISTS agent_memory_fts USING fts5(
                        Content,
                        content='agent_memory',
                        content_rowid='Id',
                        tokenize='unicode61'
                    );
                    CREATE TRIGGER IF NOT EXISTS agent_memory_ai AFTER INSERT ON agent_memory BEGIN
                        INSERT INTO agent_memory_fts(rowid, Content) VALUES (new.Id, new.Content);
                    END;
                    CREATE TRIGGER IF NOT EXISTS agent_memory_ad AFTER DELETE ON agent_memory BEGIN
                        INSERT INTO agent_memory_fts(agent_memory_fts, rowid, Content)
                        VALUES ('delete', old.Id, old.Content);
                    END;
                    CREATE TRIGGER IF NOT EXISTS agent_memory_au AFTER UPDATE OF Content ON agent_memory BEGIN
                        INSERT INTO agent_memory_fts(agent_memory_fts, rowid, Content)
                        VALUES ('delete', old.Id, old.Content);
                        INSERT INTO agent_memory_fts(rowid, Content) VALUES (new.Id, new.Content);
                    END;
                    """;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var rebuild = connection.CreateCommand())
            {
                rebuild.CommandText = "INSERT INTO agent_memory_fts(agent_memory_fts) VALUES('rebuild');";
                await rebuild.ExecuteNonQueryAsync(ct);
            }
        }
        catch
        {
            // 老 SQLite 未编 FTS5 时不影响主记忆；检索会自动退回向量 + LIKE
        }
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
        IReadOnlyCollection<string>? assetIds = null, bool includeRaw = true, int maxScanRows = 5000,
        CancellationToken ct = default, string? queryText = null)
    {
        await EnsureInitializedAsync(ct);
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
        var sql = "SELECT Source, Content, Vector, Kind, Key, Freq, Status, IFNULL(AssetIds,''), CreatedAt, IFNULL(UpdatedAt,''), Id " +
                  "FROM agent_memory WHERE 1=1";
        if (!string.IsNullOrWhiteSpace(kind))
        {
            sql += " AND Kind=@kind";
            cmd.Parameters.AddWithValue("@kind", kind);
        }
        if (!includeRaw)
            sql += " AND Kind<>'raw'";
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
        // 预过滤后按最新 Id 截断一次扫描量，避免记忆库长期增长后每轮全表载入/逐条余弦。
        sql += " ORDER BY Id DESC LIMIT @scanRows;";
        cmd.Parameters.AddWithValue("@scanRows", Math.Clamp(maxScanRows, 1, 50_000));
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var vector = ToFloats((byte[])reader.GetValue(2));
            scored.Add(new MemoryHit(
                reader.GetString(0), reader.GetString(1), VectorMath.Cosine(query, vector),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetInt64(10)));
        }

        // 向量召回是基线；有查询文本且 FTS5 可用时，用 BM25 排名做轻量混合（0.7 向量 + 0.3 词法）。
        if (!string.IsNullOrWhiteSpace(queryText))
        {
            var lexical = await SearchFtsAsync(queryText, Math.Max(50, topK * 4), ct);
            if (lexical.Count > 0)
            {
                scored = scored.Select(hit => lexical.TryGetValue(hit.Id, out var boost)
                    ? hit with { Score = hit.Score + boost * 0.2 }
                    : hit).ToList();
            }
            var phrase = queryText.Trim();
            if (phrase.Length >= 2)
                scored = scored.Select(hit => hit.Content.Contains(phrase, StringComparison.OrdinalIgnoreCase)
                    ? hit with { Score = hit.Score + 0.05 } : hit).ToList();
        }

        // 分数为主、频次为辅（同分时经验更多的条目优先）；时间衰减由 AgentMemoryService 统一处理。
        return scored
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Freq)
            .Take(Math.Max(1, topK))
            .ToList();
    }

    /// <summary>FTS5 BM25 候选：返回 rowid → 归一化词法分（0..1，排名越高越接近 1）。</summary>
    private async Task<Dictionary<long, double>> SearchFtsAsync(string queryText, int limit,
        CancellationToken ct)
    {
        var terms = ExtractTerms(queryText);
        if (terms.Count == 0)
            return new Dictionary<long, double>();
        var scores = new Dictionary<long, double>();
        await using var connection = new SqliteConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
        }
        catch
        {
            return scores;
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT rowid, bm25(agent_memory_fts) AS rank
                FROM agent_memory_fts
                WHERE agent_memory_fts MATCH @match
                ORDER BY rank
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@match", string.Join(" OR ",
                terms.Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")));
            cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
            var rows = new List<(long Id, double Rank)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetInt64(0), reader.GetDouble(1)));
            if (rows.Count > 0)
            {
                var min = rows.Min(r => r.Rank);
                var max = rows.Max(r => r.Rank);
                var range = max - min;
                foreach (var row in rows)
                    scores[row.Id] = range <= 1e-9 ? 0.9 : 1.0 - (row.Rank - min) / range;
            }
        }
        catch
        {
            // FTS5 未编译/查询语法失败：继续尝试子串匹配
        }

        // unicode61 对中文分词有限，补充按词子串匹配；仍只给已通过向量过滤的候选加分。
        try
        {
            await using var like = connection.CreateCommand();
            var clauses = new List<string>();
            for (var i = 0; i < terms.Count; i++)
            {
                clauses.Add($"Content LIKE @like{i} ESCAPE '\\'");
                like.Parameters.AddWithValue($"@like{i}", $"%{EscapeLike(terms[i])}%");
            }
            like.CommandText = "SELECT Id FROM agent_memory WHERE (" + string.Join(" OR ", clauses) +
                               ") ORDER BY Id DESC LIMIT @limit;";
            like.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
            await using var reader = await like.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                if (!scores.ContainsKey(id))
                    scores[id] = 0.9;
            }
        }
        catch
        {
            // 子串匹配失败也不影响纯向量
        }
        return scores;
    }

    private static List<string> ExtractTerms(string text) =>
        MemoryQueryRewriter.ExpandTerms(text).ToList();

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        // 清空用于更换 Embeddings 模型/维度：历史向量同样失效，一并清理，避免回滚出不可用向量。
        if (_access is null)
        {
            cmd.CommandText = "DELETE FROM memory_asset; DELETE FROM agent_incident; DELETE FROM agent_consolidation_candidate; " +
                              "DELETE FROM agent_consolidation_run; DELETE FROM agent_memory_history; " +
                              "DELETE FROM agent_memory; DELETE FROM agent_doc_state;";
        }
        else
        {
            cmd.CommandText =
                "DELETE FROM memory_asset WHERE " +
                "(OwnerKind='memory' AND OwnerId IN (SELECT Id FROM agent_memory WHERE Scope='private' AND OwnerUserId=@scopeOwner)) OR " +
                "(OwnerKind='incident' AND OwnerId IN (SELECT Id FROM agent_incident WHERE Scope='private' AND OwnerUserId=@scopeOwner)); " +
                "DELETE FROM agent_incident WHERE Scope='private' AND OwnerUserId=@scopeOwner; " +
                "DELETE FROM agent_consolidation_candidate WHERE Scope='private' AND OwnerUserId=@scopeOwner; " +
                "DELETE FROM agent_consolidation_run WHERE Scope='private' AND OwnerUserId=@scopeOwner; " +
                "DELETE FROM agent_memory_history WHERE Scope='private' AND OwnerUserId=@scopeOwner; " +
                "DELETE FROM agent_memory WHERE Scope='private' AND OwnerUserId=@scopeOwner; " +
                "DELETE FROM agent_doc_state WHERE Scope='private' AND OwnerUserId=@scopeOwner;";
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
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        long id;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
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
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        await ReplaceAssetsAsync(connection, tx, "memory", id, assetIds, ct);
        tx.Commit();
        return id;
    }

    public async Task UpdateStructuredAsync(long id, string content, float[] vector, string assetIds, string tags,
        string status, int freqIncrement, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        bool changed;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
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
            changed = await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        if (changed)
            await ReplaceAssetsAsync(connection, tx, "memory", id, assetIds, ct);
        tx.Commit();
        if (changed)
            await PruneHistoryAsync(connection, id, ct);
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
            "DELETE FROM memory_asset WHERE OwnerKind='memory' AND OwnerId IN " +
            "(SELECT Id FROM agent_memory WHERE Kind=@k AND Key=@key" + scopeFilter + "); " +
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
            "DELETE FROM memory_asset WHERE OwnerKind='memory' AND OwnerId=@id; " +
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

    public async Task<List<MemoryRecord>> ListByAssetAsync(string assetId, string? kind, int limit,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var result = new List<MemoryRecord>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var sql =
            "SELECT Id, Source, Content, Kind, Key, AssetIds, Tags, Freq, Status, CreatedAt, UpdatedAt " +
            "FROM agent_memory WHERE Kind<>'raw' AND Id IN " +
            "(SELECT OwnerId FROM memory_asset WHERE OwnerKind='memory' AND AssetId=@asset)";
        var visible = AddVisibleFilter(cmd);
        if (visible is not null) sql += " AND " + visible;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            sql += " AND Kind=@kind";
            cmd.Parameters.AddWithValue("@kind", kind);
        }
        sql += " ORDER BY UpdatedAt DESC, Id DESC LIMIT @limit;";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@asset", assetId.Trim());
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

    public string CreateDocumentKey(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var scope = _access is null || _access.IsAdmin ? "team" : "private:" + _access.OwnerUserId;
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $"doc:{scope}:{pathHash}";
    }

    public async Task<bool> IsDocumentCurrentAsync(string docKey, string hash, string model,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM agent_doc_state WHERE DocKey=@k AND Hash=@h AND Model=@m LIMIT 1;";
        cmd.Parameters.AddWithValue("@k", docKey);
        cmd.Parameters.AddWithValue("@h", hash);
        cmd.Parameters.AddWithValue("@m", model);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task ReplaceDocumentChunksAsync(string docKey, string root, string legacySource, string hash,
        string model, IReadOnlyList<string> sources, IReadOnlyList<string> contents, IReadOnlyList<float[]> vectors,
        CancellationToken ct = default)
    {
        if (sources.Count != contents.Count || contents.Count != vectors.Count)
            throw new ArgumentException("文档分块、内容与向量数量不一致");

        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        var owner = _access?.OwnerUserId ?? string.Empty;
        var scope = _access?.WriteScope ?? "team";

        // 旧 chunk（Key=docKey）与旧版整文件索引（Source=legacySource, Key 为空）一并清理，避免召回过期内容。
        await using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            var writable = AddWritableFilter(del);
            del.CommandText = "DELETE FROM agent_memory WHERE Kind='raw' AND " +
                              "(Key=@docKey OR (Source=@legacy AND IFNULL(Key,'')=''))" +
                              (writable is null ? string.Empty : " AND " + writable) + ";";
            del.Parameters.AddWithValue("@docKey", docKey);
            del.Parameters.AddWithValue("@legacy", legacySource);
            await del.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT INTO agent_memory(Source, Content, Vector, CreatedAt, Kind, Key, AssetIds, Tags, Freq, UpdatedAt, Status, OwnerUserId, Scope, Version) " +
                "VALUES (@s, @c, @v, @now, 'raw', @key, '', '', 1, @now, 'active', @owner, @scope, 1);";
            var pSource = insert.Parameters.Add("@s", SqliteType.Text);
            var pContent = insert.Parameters.Add("@c", SqliteType.Text);
            var pVector = insert.Parameters.Add("@v", SqliteType.Blob);
            insert.Parameters.AddWithValue("@now", now);
            insert.Parameters.AddWithValue("@key", docKey);
            insert.Parameters.AddWithValue("@owner", owner);
            insert.Parameters.AddWithValue("@scope", scope);
            for (var i = 0; i < contents.Count; i++)
            {
                pSource.Value = sources[i];
                pContent.Value = contents[i];
                pVector.Value = ToBytes(vectors[i]);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = tx;
            state.CommandText =
                "INSERT INTO agent_doc_state(DocKey, Root, Hash, Model, ChunkCount, UpdatedAt, OwnerUserId, Scope) " +
                "VALUES(@k, @root, @h, @m, @n, @now, @owner, @scope) " +
                "ON CONFLICT(DocKey) DO UPDATE SET Root=excluded.Root, Hash=excluded.Hash, Model=excluded.Model, " +
                "ChunkCount=excluded.ChunkCount, UpdatedAt=excluded.UpdatedAt;";
            state.Parameters.AddWithValue("@k", docKey);
            state.Parameters.AddWithValue("@root", root);
            state.Parameters.AddWithValue("@h", hash);
            state.Parameters.AddWithValue("@m", model);
            state.Parameters.AddWithValue("@n", contents.Count);
            state.Parameters.AddWithValue("@now", now);
            state.Parameters.AddWithValue("@owner", owner);
            state.Parameters.AddWithValue("@scope", scope);
            await state.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
    }

    public async Task<long> AddIncidentAsync(string title, string symptom, string assetIds, string rootCause,
        string actions, string outcome, string evidence, double confidence, string status, string sessionId,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        long id;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO agent_incident(Title, Symptom, AssetIds, RootCause, Actions, Outcome, Evidence, Confidence, Status, SessionId, OwnerUserId, Scope, CreatedAt, UpdatedAt) " +
                "VALUES (@title, @symptom, @assets, @root, @actions, @outcome, @evidence, @confidence, @status, @session, @owner, @scope, @now, @now); " +
                "SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@symptom", symptom);
            cmd.Parameters.AddWithValue("@assets", assetIds);
            cmd.Parameters.AddWithValue("@root", rootCause);
            cmd.Parameters.AddWithValue("@actions", actions);
            cmd.Parameters.AddWithValue("@outcome", outcome);
            cmd.Parameters.AddWithValue("@evidence", evidence);
            cmd.Parameters.AddWithValue("@confidence", confidence);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@session", sessionId);
            cmd.Parameters.AddWithValue("@owner", _access?.OwnerUserId ?? string.Empty);
            cmd.Parameters.AddWithValue("@scope", _access?.WriteScope ?? "team");
            cmd.Parameters.AddWithValue("@now", now);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        await ReplaceAssetsAsync(connection, tx, "incident", id, assetIds, ct);
        tx.Commit();
        return id;
    }

    public async Task<IncidentRecord?> GetIncidentAsync(long id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var visible = AddVisibleFilter(cmd);
        cmd.CommandText =
            "SELECT Id, Title, Symptom, AssetIds, RootCause, Actions, Outcome, Evidence, Confidence, Status, SessionId, CreatedAt, UpdatedAt, SupersededBy " +
            "FROM agent_incident WHERE Id=@id" +
            (visible is null ? string.Empty : " AND " + visible) + " LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadIncident(reader) : null;
    }

    public async Task<List<IncidentRecord>> ListIncidentsAsync(string? assetId, string? status, int limit,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var result = new List<IncidentRecord>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var sql =
            "SELECT Id, Title, Symptom, AssetIds, RootCause, Actions, Outcome, Evidence, Confidence, Status, SessionId, CreatedAt, UpdatedAt, SupersededBy " +
            "FROM agent_incident WHERE 1=1";
        var visible = AddVisibleFilter(cmd);
        if (visible is not null)
            sql += " AND " + visible;
        if (!string.IsNullOrWhiteSpace(status))
        {
            sql += " AND Status=@status";
            cmd.Parameters.AddWithValue("@status", status);
        }
        if (!string.IsNullOrWhiteSpace(assetId))
        {
            sql += " AND EXISTS (SELECT 1 FROM memory_asset ma WHERE ma.OwnerKind='incident' AND ma.OwnerId=agent_incident.Id AND ma.AssetId=@asset)";
            cmd.Parameters.AddWithValue("@asset", assetId.Trim());
        }
        sql += " ORDER BY UpdatedAt DESC, Id DESC LIMIT @limit;";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadIncident(reader));
        return result;
    }

    public async Task<bool> SupersedeIncidentAsync(long oldId, long newId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var writable = AddWritableFilter(cmd);
        cmd.CommandText =
            "UPDATE agent_incident SET Status='superseded', SupersededBy=@new, UpdatedAt=@now " +
            "WHERE Id=@old AND Status<>'superseded'" +
            (writable is null ? string.Empty : " AND " + writable) + ";";
        cmd.Parameters.AddWithValue("@new", newId);
        cmd.Parameters.AddWithValue("@old", oldId);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<List<string>> ListMemoryAssetIdsAsync(string ownerKind, long ownerId,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var result = new List<string>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT AssetId FROM memory_asset WHERE OwnerKind=@kind AND OwnerId=@id ORDER BY AssetId;";
        cmd.Parameters.AddWithValue("@kind", ownerKind);
        cmd.Parameters.AddWithValue("@id", ownerId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task<List<RawMemoryRecord>> ListUnconsolidatedRawAsync(int limit, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var result = new List<RawMemoryRecord>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var visible = AddVisibleFilter(cmd);
        cmd.CommandText =
            "SELECT Id, Source, Content, CreatedAt FROM agent_memory " +
            "WHERE Kind='raw' AND IFNULL(ConsolidatedAt,'')='' AND Source LIKE '会话%'" +
            (visible is null ? string.Empty : " AND " + visible) +
            " ORDER BY Id LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new RawMemoryRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    public async Task<int> MarkRawConsolidatedAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return 0;
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        var changed = 0;
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        var writable = AddWritableFilter(cmd);
        cmd.CommandText = "UPDATE agent_memory SET ConsolidatedAt=@now WHERE Id=@id AND Kind='raw'" +
                          (writable is null ? string.Empty : " AND " + writable) + ";";
        cmd.Parameters.AddWithValue("@now", now);
        var idParam = cmd.Parameters.Add("@id", SqliteType.Integer);
        foreach (var id in ids)
        {
            idParam.Value = id;
            changed += await cmd.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return changed;
    }

    public async Task<long?> AddConsolidationCandidateAsync(string kind, string payloadJson, string assetIds,
        string evidence, double confidence, string status, string sourceRefs, string dedupeKey,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var now = DateTime.UtcNow.ToString("O");
        cmd.CommandText =
            "INSERT INTO agent_consolidation_candidate(Kind, PayloadJson, AssetIds, Evidence, Confidence, Status, SourceRefs, DedupeKey, OwnerUserId, Scope, CreatedAt, UpdatedAt) " +
            "VALUES(@kind, @payload, @assets, @evidence, @confidence, @status, @refs, @dedupe, @owner, @scope, @now, @now); " +
            "SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@payload", payloadJson);
        cmd.Parameters.AddWithValue("@assets", assetIds);
        cmd.Parameters.AddWithValue("@evidence", evidence);
        cmd.Parameters.AddWithValue("@confidence", confidence);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@refs", sourceRefs);
        cmd.Parameters.AddWithValue("@dedupe", dedupeKey);
        cmd.Parameters.AddWithValue("@owner", _access?.OwnerUserId ?? string.Empty);
        cmd.Parameters.AddWithValue("@scope", _access?.WriteScope ?? "team");
        cmd.Parameters.AddWithValue("@now", now);
        try
        {
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return null;   // DedupeKey 已存在
        }
    }

    public async Task<ConsolidationCandidate?> GetConsolidationCandidateAsync(long id,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var visible = AddVisibleFilter(cmd);
        cmd.CommandText =
            "SELECT Id, Kind, PayloadJson, AssetIds, Evidence, Confidence, Status, SourceRefs, CreatedAt, UpdatedAt, AppliedRef, RejectReason " +
            "FROM agent_consolidation_candidate WHERE Id=@id" +
            (visible is null ? string.Empty : " AND " + visible) + " LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ConsolidationCandidate(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetDouble(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11))
            : null;
    }

    public async Task<List<ConsolidationCandidate>> ListConsolidationCandidatesAsync(string? status, int limit,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var result = new List<ConsolidationCandidate>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var sql =
            "SELECT Id, Kind, PayloadJson, AssetIds, Evidence, Confidence, Status, SourceRefs, CreatedAt, UpdatedAt, AppliedRef, RejectReason " +
            "FROM agent_consolidation_candidate WHERE 1=1";
        var visible = AddVisibleFilter(cmd);
        if (visible is not null)
            sql += " AND " + visible;
        if (!string.IsNullOrWhiteSpace(status))
        {
            sql += " AND Status=@status";
            cmd.Parameters.AddWithValue("@status", status);
        }
        sql += " ORDER BY Id DESC LIMIT @limit;";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ConsolidationCandidate(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetDouble(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11)));
        }
        return result;
    }

    public async Task<bool> UpdateConsolidationCandidateAsync(long id, string status, string appliedRef,
        string rejectReason, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var writable = AddWritableFilter(cmd);
        cmd.CommandText =
            "UPDATE agent_consolidation_candidate SET Status=@status, AppliedRef=@ref, RejectReason=@reason, UpdatedAt=@now " +
            "WHERE Id=@id" + (writable is null ? string.Empty : " AND " + writable) + ";";
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@ref", appliedRef);
        cmd.Parameters.AddWithValue("@reason", rejectReason);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<long> StartConsolidationRunAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO agent_consolidation_run(StartedAt, Status, OwnerUserId, Scope) VALUES(@now, 'running', @owner, @scope); " +
            "SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@owner", _access?.OwnerUserId ?? string.Empty);
        cmd.Parameters.AddWithValue("@scope", _access?.WriteScope ?? "team");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task FinishConsolidationRunAsync(long id, string status, int rawCount, int candidateCount,
        int appliedCount, string error, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var writable = AddWritableFilter(cmd);
        cmd.CommandText =
            "UPDATE agent_consolidation_run SET FinishedAt=@now, Status=@status, RawCount=@raw, CandidateCount=@cand, " +
            "AppliedCount=@applied, Error=@error WHERE Id=@id" +
            (writable is null ? string.Empty : " AND " + writable) + ";";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@raw", rawCount);
        cmd.Parameters.AddWithValue("@cand", candidateCount);
        cmd.Parameters.AddWithValue("@applied", appliedCount);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(int Runs, int RawCount)> GetConsolidationUsageSinceAsync(DateTimeOffset since,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var visible = AddVisibleFilter(cmd);
        cmd.CommandText = "SELECT COUNT(*), IFNULL(SUM(RawCount),0) FROM agent_consolidation_run " +
                          "WHERE StartedAt>=@since" + (visible is null ? string.Empty : " AND " + visible) + ";";
        cmd.Parameters.AddWithValue("@since", since.UtcDateTime.ToString("O"));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (0, 0);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    public async Task<List<ConsolidationRunRecord>> ListConsolidationRunsAsync(int limit,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var visible = AddVisibleFilter(cmd);
        cmd.CommandText =
            "SELECT Id, StartedAt, FinishedAt, Status, RawCount, CandidateCount, AppliedCount, Error, OwnerUserId, Scope " +
            "FROM agent_consolidation_run WHERE 1=1" +
            (visible is null ? string.Empty : " AND " + visible) +
            " ORDER BY StartedAt DESC, Id DESC LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
        var list = new List<ConsolidationRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ConsolidationRunRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9)));
        }
        return list;
    }

    private static async Task ReplaceAssetsAsync(SqliteConnection connection, SqliteTransaction tx,
        string ownerKind, long ownerId, string? assetIds, CancellationToken ct)
    {
        await using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM memory_asset WHERE OwnerKind=@kind AND OwnerId=@id;";
            del.Parameters.AddWithValue("@kind", ownerKind);
            del.Parameters.AddWithValue("@id", ownerId);
            await del.ExecuteNonQueryAsync(ct);
        }

        foreach (var asset in ParseAssetIds(assetIds))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO memory_asset(OwnerKind, OwnerId, AssetId) VALUES(@kind, @id, @asset);";
            insert.Parameters.AddWithValue("@kind", ownerKind);
            insert.Parameters.AddWithValue("@id", ownerId);
            insert.Parameters.AddWithValue("@asset", asset);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static List<string> ParseAssetIds(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(new[] { ',', ';', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList();

    private static IncidentRecord ReadIncident(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.GetDouble(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
        reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetInt64(13));

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

    public async Task<int> PruneHistoryAsync(int keep, long? memoryId = null, CancellationToken ct = default)
    {
        keep = Math.Clamp(keep, 1, 1000);
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var visible = AddVisibleFilter(cmd);
        var scopeFilter = visible is null ? string.Empty : " AND " + visible;
        var idFilter = memoryId is null ? string.Empty : " AND MemoryId=@id";
        cmd.CommandText =
            "DELETE FROM agent_memory_history WHERE Id IN (" +
            "SELECT Id FROM (SELECT Id, ROW_NUMBER() OVER (PARTITION BY MemoryId ORDER BY Version DESC, Id DESC) AS rn " +
            "FROM agent_memory_history WHERE 1=1" + idFilter + scopeFilter + ") WHERE rn > @keep);";
        cmd.Parameters.AddWithValue("@keep", keep);
        if (memoryId is not null)
            cmd.Parameters.AddWithValue("@id", memoryId.Value);
        var deleted = await cmd.ExecuteNonQueryAsync(ct);

        if (memoryId is null)
        {
            // 全局清理时顺带删除「记忆已不存在且超过保留期」的孤儿历史。
            await using var orphan = connection.CreateCommand();
            var orphanVisible = AddVisibleFilter(orphan);
            orphan.CommandText =
                "DELETE FROM agent_memory_history WHERE MemoryId NOT IN (SELECT Id FROM agent_memory) " +
                "AND ArchivedAt < @cutoff" +
                (orphanVisible is null ? string.Empty : " AND " + orphanVisible) + ";";
            orphan.Parameters.AddWithValue("@cutoff",
                DateTime.UtcNow.AddDays(-OrphanHistoryRetentionDays).ToString("O"));
            deleted += await orphan.ExecuteNonQueryAsync(ct);
        }

        return deleted;
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
        var restored = await write.ExecuteNonQueryAsync(ct) > 0;
        if (restored)
            await PruneHistoryAsync(connection, memoryId, ct);
        return restored;
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
        var archived = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (archived)
            await PruneHistoryAsync(connection, id, ct);
        return archived;
    }

    /// <summary>同一连接内按 MemoryId 保留最近 N 版（DefaultHistoryKeep）。</summary>
    private static async Task PruneHistoryAsync(SqliteConnection connection, long memoryId, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "DELETE FROM agent_memory_history WHERE MemoryId=@id AND Id NOT IN (" +
            "SELECT Id FROM agent_memory_history WHERE MemoryId=@id ORDER BY Version DESC, Id DESC LIMIT @keep);";
        cmd.Parameters.AddWithValue("@id", memoryId);
        cmd.Parameters.AddWithValue("@keep", DefaultHistoryKeep);
        await cmd.ExecuteNonQueryAsync(ct);
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
