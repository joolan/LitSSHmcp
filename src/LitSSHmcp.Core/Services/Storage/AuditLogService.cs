using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Core.Services.Storage;

public class AuditLogService : IAuditLogService
{
    // 链首的前置哈希（64 个 0）
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// 审计存储/哈希链格式版本（写入 PRAGMA user_version）。
    /// v2：哈希链 payload 纳入 SessionId（会话 ID 参与防篡改）。
    /// v3：新增 Tool（产生记录的 MCP 工具名）并纳入哈希链。
    /// v4：新增 Category（执行/审批/探测/列表/传输）与 Decision（审批决策），并纳入哈希链。
    /// v5：SQL/Redis 审计同样新增 Category 与 Decision，并纳入哈希链。
    /// v6：新增 UserId（操作用户归因：Web 登录用户 Id / 桌面未登录 "local"），并纳入哈希链 payload。
    /// v7：AuditChain 新增 Algorithm 列（hmac-sha256 / sha256），不再依赖实例是否初始化过密钥；
    ///     历史无密钥记录保留原哈希并标记为强度降级，校验时给出警告而不是误报篡改。
    /// 版本落后时启动会补齐缺失列并**保留记录**，再按当前 payload 重建/回填哈希链；
    /// 更老版本不再清空审计库（避免升级丢历史）。
    /// </summary>
    private const int AuditFormatVersion = 7;

    /// <summary>v6 及更早版本的链结构版本：低于它才需要按当前 payload 重建整条链。</summary>
    private const int ChainRebuildVersion = 6;

    private const string AlgorithmHmac = "hmac-sha256";
    private const string AlgorithmSha256 = "sha256";

    private readonly string _dbPath;
    private readonly string _lockPath;
    private readonly ISecurityOptionsProvider? _securityOptions;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;
    private byte[]? _signingKey;   // 为 null 时退化为纯 SHA-256

    /// <summary>跨进程写锁（<c>audit.db.chain.lock</c>）的等待上限；超过视为异常，宁可写失败也不允许链分叉。</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>当前 MCP 会话 ID（启动时由宿主设置），写入时自动补全到每条记录。</summary>
    public string? SessionId { get; set; }

    public AuditLogService() : this(null, null)
    {
    }

    public AuditLogService(ISecurityOptionsProvider? securityOptions) : this(securityOptions, null)
    {
    }

    public AuditLogService(ISecurityOptionsProvider? securityOptions, string? dbPath)
    {
        _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.AuditDb : dbPath!;
        _lockPath = _dbPath + ".chain.lock";
        _securityOptions = securityOptions;
    }

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    /// <summary>写入/初始化专用连接：命令超时放宽，配合 busy_timeout 让并发写排队而不是直接失败。</summary>
    private string WriteConnectionString => $"Data Source={_dbPath};Default Timeout=10";

    /// <summary>进入临界段：先拿进程内闸门，再拿跨进程文件锁（顺序固定，避免死锁）。</summary>
    private async Task<CrossProcessFileLock> EnterWriteAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CrossProcessFileLock.AcquireAsync(_lockPath, LockTimeout, ct).ConfigureAwait(false);
        }
        catch
        {
            _writeGate.Release();
            throw;
        }
    }

    private void ExitWrite(CrossProcessFileLock? gate)
    {
        gate?.Dispose();
        _writeGate.Release();
    }

    /// <summary>写连接专用的 busy 等待（避免他进程持写锁时直接抛 SQLITE_BUSY）。</summary>
    private static async Task SetBusyTimeoutAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>初始化（可重复调用）：建表/迁移/密钥加载/归档搬迁。</summary>
    public async Task InitializeAsync()
    {
        // 初始化包含建表/版本重置/密钥文件创建/归档搬迁，必须跨进程串行：
        // 否则两个子进程同时启动会各自生成不同签名密钥（链哈希互不匹配）或互相清空对方刚写入的链。
        var gate = await EnterWriteAsync();
        try
        {
            await InitializeCoreAsync();
            _initialized = true;
        }
        finally
        {
            ExitWrite(gate);
        }
    }

    /// <summary>
    /// 首次写入/校验/清理前确保初始化完成。审计签名不应取决于调用方是否提前 await 过 InitializeAsync：
    /// 应用锁、终端 AI、触发器等短生命周期实例可能直接写入，之前会静默退化为无密钥 SHA-256。
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_initialized)
                await InitializeAsync().ConfigureAwait(false);
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task InitializeCoreAsync()
    {
        await using var connection = new SqliteConnection(WriteConnectionString);
        await connection.OpenAsync();
        await SetBusyTimeoutAsync(connection);

        var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS AuditLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ServerId TEXT NOT NULL,
                ServerName TEXT NOT NULL,
                Command TEXT NOT NULL,
                Result TEXT,
                Status TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                ExitCode INTEGER,
                IsFileTransfer INTEGER NOT NULL DEFAULT 0,
                FilePath TEXT,
                FileSize INTEGER,
                SessionId TEXT,
                Tool TEXT,
                Category TEXT,
                Decision TEXT,
                UserId TEXT
            )";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_AuditLogs_Timestamp ON AuditLogs(Timestamp DESC);";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_AuditLogs_ServerId ON AuditLogs(ServerId, Timestamp DESC);";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS SqlAuditLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DataSourceId TEXT NOT NULL,
                DataSourceName TEXT NOT NULL,
                Operation TEXT NOT NULL,
                Sql TEXT NOT NULL,
                Status TEXT NOT NULL,
                Result TEXT,
                RowsAffected INTEGER,
                DurationMs REAL,
                Timestamp TEXT NOT NULL,
                SessionId TEXT,
                Tool TEXT,
                Category TEXT,
                Decision TEXT,
                UserId TEXT
            )";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_SqlAuditLogs_Timestamp ON SqlAuditLogs(Timestamp DESC);";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_SqlAuditLogs_DataSourceId ON SqlAuditLogs(DataSourceId, Timestamp DESC);";
        await cmd.ExecuteNonQueryAsync();

        // 历史归档表（与活动表同构，用于"超期记录永久保留"）
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS AuditLogsHistory (
                Id INTEGER PRIMARY KEY,
                ServerId TEXT NOT NULL,
                ServerName TEXT NOT NULL,
                Command TEXT NOT NULL,
                Result TEXT,
                Status TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                ExitCode INTEGER,
                IsFileTransfer INTEGER NOT NULL DEFAULT 0,
                FilePath TEXT,
                FileSize INTEGER,
                SessionId TEXT,
                Tool TEXT,
                Category TEXT,
                Decision TEXT,
                UserId TEXT
            )";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_AuditLogsHistory_Timestamp ON AuditLogsHistory(Timestamp DESC);";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS SqlAuditLogsHistory (
                Id INTEGER PRIMARY KEY,
                DataSourceId TEXT NOT NULL,
                DataSourceName TEXT NOT NULL,
                Operation TEXT NOT NULL,
                Sql TEXT NOT NULL,
                Status TEXT NOT NULL,
                Result TEXT,
                RowsAffected INTEGER,
                DurationMs REAL,
                Timestamp TEXT NOT NULL,
                SessionId TEXT,
                Tool TEXT,
                Category TEXT,
                Decision TEXT,
                UserId TEXT
            )";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_SqlAuditLogsHistory_Timestamp ON SqlAuditLogsHistory(Timestamp DESC);";
        await cmd.ExecuteNonQueryAsync();

        // 哈希链
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS AuditChain (
                Seq INTEGER PRIMARY KEY AUTOINCREMENT,
                Kind TEXT NOT NULL,
                RowId INTEGER NOT NULL,
                RecordHash TEXT NOT NULL,
                PrevHash TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Algorithm TEXT
            )";
        await cmd.ExecuteNonQueryAsync();

        // v7：为旧链补算法列（不重建链；旧行 NULL，由迁移回填或校验时自动识别）。
        EnsureColumn(connection, "AuditChain", "Algorithm", "TEXT");

        // 旧库迁移：为已存在的表补 SessionId 列（新库在 CREATE 时已包含；ALTER ADD 加在末尾，
        // 保证活动表/历史表列顺序一致，`INSERT ... SELECT *` 才能继续工作）。
        EnsureColumn(connection, "AuditLogs", "SessionId", "TEXT");
        EnsureColumn(connection, "AuditLogsHistory", "SessionId", "TEXT");
        EnsureColumn(connection, "SqlAuditLogs", "SessionId", "TEXT");
        EnsureColumn(connection, "SqlAuditLogsHistory", "SessionId", "TEXT");
        EnsureColumn(connection, "AuditLogs", "Tool", "TEXT");
        EnsureColumn(connection, "AuditLogsHistory", "Tool", "TEXT");
        EnsureColumn(connection, "SqlAuditLogs", "Tool", "TEXT");
        EnsureColumn(connection, "SqlAuditLogsHistory", "Tool", "TEXT");
        // v4：事件类型 + 审批决策（活动表/历史表必须同序追加，保证 `INSERT ... SELECT *` 仍可用）
        EnsureColumn(connection, "AuditLogs", "Category", "TEXT");
        EnsureColumn(connection, "AuditLogsHistory", "Category", "TEXT");
        EnsureColumn(connection, "AuditLogs", "Decision", "TEXT");
        EnsureColumn(connection, "AuditLogsHistory", "Decision", "TEXT");
        EnsureColumn(connection, "SqlAuditLogs", "Category", "TEXT");
        EnsureColumn(connection, "SqlAuditLogsHistory", "Category", "TEXT");
        EnsureColumn(connection, "SqlAuditLogs", "Decision", "TEXT");
        EnsureColumn(connection, "SqlAuditLogsHistory", "Decision", "TEXT");
        // v6：操作用户归因（活动表/历史表必须同序追加，保证 `INSERT ... SELECT *` 仍可用）
        EnsureColumn(connection, "AuditLogs", "UserId", "TEXT");
        EnsureColumn(connection, "AuditLogsHistory", "UserId", "TEXT");
        EnsureColumn(connection, "SqlAuditLogs", "UserId", "TEXT");
        EnsureColumn(connection, "SqlAuditLogsHistory", "UserId", "TEXT");

        // MCP 会话表：SessionId -> 客户端名称/版本/首末活动时间
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Sessions (
                SessionId TEXT PRIMARY KEY,
                ClientName TEXT,
                ClientVersion TEXT,
                StartedAt TEXT NOT NULL,
                LastSeenAt TEXT NOT NULL,
                OwnerUserId TEXT
            )";
        await cmd.ExecuteNonQueryAsync();

        // P0-2：旧库补会话归属列；历史会话保持 NULL（管理员可见，普通用户不可见）
        EnsureColumn(connection, "Sessions", "OwnerUserId", "TEXT");

        cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Sessions_LastSeenAt ON Sessions(LastSeenAt DESC);";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Sessions_Owner_LastSeen ON Sessions(OwnerUserId, LastSeenAt DESC);";
        await cmd.ExecuteNonQueryAsync();

        // 签名密钥必须先加载：v5→v6 迁移要用它与后续校验一致的 HMAC 重算链哈希。
        _signingKey = LoadOrCreateSigningKey(connection);

        // 审计格式版本升级：
        // v5 → v6：保留全部历史记录（补 UserId 列并按新 payload 重建哈希链，链连续性从头开始）；
        // 更老版本（<5）：同样补齐缺失列并保留记录，按当前 payload 重建哈希链（不再清空审计库）。
        UpgradeFormat(connection);

        ArchiveExpired(connection);
    }

    // —— 写入（活动表 + 链，同一事务） ——

    public async Task LogCommandAsync(CommandAuditLog log)
    {
        await EnsureInitializedAsync();
        var gate = await EnterWriteAsync();
        try
        {
            await using var connection = new SqliteConnection(WriteConnectionString);
            await connection.OpenAsync();
            await SetBusyTimeoutAsync(connection);
            using var tx = connection.BeginTransaction();

            var (command, commandRedacted) = SecretRedactor.RedactWithChange(log.Command);
            var (result, resultRedacted) = SecretRedactor.RedactWithChange(log.Result);
            var (filePath, pathRedacted) = SecretRedactor.RedactWithChange(log.FilePath);
            if (commandRedacted || resultRedacted || pathRedacted)
                result = AppendRedactionMarker(result);

            long id;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"
                    INSERT INTO AuditLogs (ServerId, ServerName, Command, Result, Status, Timestamp, ExitCode, IsFileTransfer, FilePath, FileSize, SessionId, Tool, Category, Decision, UserId)
                    VALUES (@ServerId, @ServerName, @Command, @Result, @Status, @Timestamp, @ExitCode, @IsFileTransfer, @FilePath, @FileSize, @SessionId, @Tool, @Category, @Decision, @UserId);
                    SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("@ServerId", log.ServerId);
                insert.Parameters.AddWithValue("@ServerName", log.ServerName);
                insert.Parameters.AddWithValue("@Command", command);
                insert.Parameters.AddWithValue("@Result", string.IsNullOrEmpty(result) ? DBNull.Value : result);
                insert.Parameters.AddWithValue("@Status", log.Status.ToString());
                insert.Parameters.AddWithValue("@Timestamp", log.Timestamp.ToString("O"));
                insert.Parameters.AddWithValue("@ExitCode", log.ExitCode ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@IsFileTransfer", log.IsFileTransfer ? 1 : 0);
                insert.Parameters.AddWithValue("@FilePath", string.IsNullOrEmpty(filePath) ? DBNull.Value : filePath);
                insert.Parameters.AddWithValue("@FileSize", log.FileSize ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@SessionId", (log.SessionId ?? SessionId) ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@Tool", (log.Tool ?? AuditContext.CurrentTool) ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@Category", CategoryName(log));
                insert.Parameters.AddWithValue("@Decision", log.Decision ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@UserId", ResolveUserId(log.UserId));
                id = (long)(await insert.ExecuteScalarAsync())!;
            }

            var payload = await ReadCommandPayloadAsync(connection, tx, id);
            await AppendChainAsync(connection, tx, "cmd", id, payload ?? string.Empty);
            tx.Commit();
        }
        finally
        {
            ExitWrite(gate);
        }
    }

    public async Task LogSqlAsync(SqlAuditLog log)
    {
        await EnsureInitializedAsync();
        var gate = await EnterWriteAsync();
        try
        {
            await using var connection = new SqliteConnection(WriteConnectionString);
            await connection.OpenAsync();
            await SetBusyTimeoutAsync(connection);
            using var tx = connection.BeginTransaction();

            var (sql, sqlRedacted) = SecretRedactor.RedactWithChange(log.Sql);
            var (result, resultRedacted) = SecretRedactor.RedactWithChange(log.Result);
            if (sqlRedacted || resultRedacted)
                result = AppendRedactionMarker(result);

            long id;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"
                    INSERT INTO SqlAuditLogs (DataSourceId, DataSourceName, Operation, Sql, Status, Result, RowsAffected, DurationMs, Timestamp, SessionId, Tool, Category, Decision, UserId)
                    VALUES (@DataSourceId, @DataSourceName, @Operation, @Sql, @Status, @Result, @RowsAffected, @DurationMs, @Timestamp, @SessionId, @Tool, @Category, @Decision, @UserId);
                    SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("@DataSourceId", log.DataSourceId);
                insert.Parameters.AddWithValue("@DataSourceName", log.DataSourceName);
                insert.Parameters.AddWithValue("@Operation", log.Operation.ToString());
                insert.Parameters.AddWithValue("@Sql", sql);
                insert.Parameters.AddWithValue("@Status", log.Status.ToString());
                insert.Parameters.AddWithValue("@Result", string.IsNullOrEmpty(result) ? DBNull.Value : result);
                insert.Parameters.AddWithValue("@RowsAffected", log.RowsAffected ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@DurationMs", log.DurationMs ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@Timestamp", log.Timestamp.ToString("O"));
                insert.Parameters.AddWithValue("@SessionId", (log.SessionId ?? SessionId) ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@Tool", (log.Tool ?? AuditContext.CurrentTool) ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@Category", log.Category.ToString());
                insert.Parameters.AddWithValue("@Decision", log.Decision ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@UserId", ResolveUserId(log.UserId));
                id = (long)(await insert.ExecuteScalarAsync())!;
            }

            var payload = await ReadSqlPayloadAsync(connection, tx, id);
            await AppendChainAsync(connection, tx, "sql", id, payload ?? string.Empty);
            tx.Commit();
        }
        finally
        {
            ExitWrite(gate);
        }
    }

    // —— 查询（活动表 / 含历史归档） ——

    public async Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null, AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null, string? toolPrefix = null)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var conditions = new List<string>();
        var cmd = connection.CreateCommand();

        if (!string.IsNullOrWhiteSpace(serverId))
        {
            conditions.Add("ServerId = @ServerId");
            cmd.Parameters.AddWithValue("@ServerId", serverId);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            conditions.Add("(Command LIKE @Keyword ESCAPE '\\' OR IFNULL(UserId, '') LIKE @Keyword ESCAPE '\\')");
            cmd.Parameters.AddWithValue("@Keyword", $"%{EscapeLike(keyword.Trim())}%");
        }

        AddUserIdsCondition(conditions, cmd, userIds);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            conditions.Add("SessionId = @SessionId");
            cmd.Parameters.AddWithValue("@SessionId", sessionId);
        }

        if (!string.IsNullOrWhiteSpace(tool))
        {
            conditions.Add("Tool = @Tool");
            cmd.Parameters.AddWithValue("@Tool", tool);
        }

        if (!string.IsNullOrWhiteSpace(toolPrefix))
        {
            conditions.Add("Tool LIKE @ToolPrefix ESCAPE '\\'");
            cmd.Parameters.AddWithValue("@ToolPrefix", EscapeLike(toolPrefix.Trim()) + "%");
        }

        if (category is { } cat)
        {
            conditions.Add("Category = @Category");
            cmd.Parameters.AddWithValue("@Category", cat.ToString());
        }

        var source = includeHistory
            ? "(SELECT * FROM AuditLogs UNION ALL SELECT * FROM AuditLogsHistory)"
            : "AuditLogs";
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        cmd.CommandText = $"SELECT * FROM {source} {where} ORDER BY Timestamp DESC, Id DESC LIMIT @Limit OFFSET @Offset";
        cmd.Parameters.AddWithValue("@Limit", Math.Max(1, limit));
        cmd.Parameters.AddWithValue("@Offset", Math.Max(0, offset));

        var logs = new List<CommandAuditLog>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            logs.Add(ReadCommandRow(reader));

        return logs.ToArray();
    }

    public async Task<SqlAuditLog[]> GetSqlLogsAsync(string? dataSourceId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null, AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null, string? toolPrefix = null)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var conditions = new List<string>();
        var cmd = connection.CreateCommand();

        if (!string.IsNullOrWhiteSpace(dataSourceId))
        {
            conditions.Add("DataSourceId = @DataSourceId");
            cmd.Parameters.AddWithValue("@DataSourceId", dataSourceId);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            conditions.Add("(Sql LIKE @Keyword ESCAPE '\\' OR IFNULL(UserId, '') LIKE @Keyword ESCAPE '\\')");
            cmd.Parameters.AddWithValue("@Keyword", $"%{EscapeLike(keyword.Trim())}%");
        }

        AddUserIdsCondition(conditions, cmd, userIds);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            conditions.Add("SessionId = @SessionId");
            cmd.Parameters.AddWithValue("@SessionId", sessionId);
        }

        if (!string.IsNullOrWhiteSpace(tool))
        {
            conditions.Add("Tool = @Tool");
            cmd.Parameters.AddWithValue("@Tool", tool);
        }

        if (!string.IsNullOrWhiteSpace(toolPrefix))
        {
            conditions.Add("Tool LIKE @ToolPrefix ESCAPE '\\'");
            cmd.Parameters.AddWithValue("@ToolPrefix", EscapeLike(toolPrefix.Trim()) + "%");
        }

        if (category is { } scat)
        {
            conditions.Add("Category = @Category");
            cmd.Parameters.AddWithValue("@Category", scat.ToString());
        }

        var source = includeHistory
            ? "(SELECT * FROM SqlAuditLogs UNION ALL SELECT * FROM SqlAuditLogsHistory)"
            : "SqlAuditLogs";
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        cmd.CommandText = $"SELECT * FROM {source} {where} ORDER BY Timestamp DESC, Id DESC LIMIT @Limit OFFSET @Offset";
        cmd.Parameters.AddWithValue("@Limit", Math.Max(1, limit));
        cmd.Parameters.AddWithValue("@Offset", Math.Max(0, offset));

        var logs = new List<SqlAuditLog>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            logs.Add(ReadSqlRow(reader));

        return logs.ToArray();
    }

    /// <summary>
    /// 按归因用户集合过滤（用户名搜索 → 映射到用户 Id 后传入）：<c>UserId IN (…)</c>。
    /// 空集合不产生条件。
    /// </summary>
    private static void AddUserIdsCondition(List<string> conditions, SqliteCommand cmd, IReadOnlyCollection<string>? userIds)
    {
        if (userIds is not { Count: > 0 })
            return;

        var names = new List<string>();
        var index = 0;
        foreach (var id in userIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            var parameter = "@Uid" + index++;
            names.Add(parameter);
            cmd.Parameters.AddWithValue(parameter, id);
        }

        if (names.Count > 0)
            conditions.Add($"UserId IN ({string.Join(", ", names)})");
    }

    /// <summary>转义 LIKE 通配符，避免 keyword 里的 % / _ 匹配到非预期记录。</summary>
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // —— 会话 ——

    public async Task RecordSessionAsync(AuditSession session)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId))
            return;

        await EnsureInitializedAsync();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Sessions (SessionId, ClientName, ClientVersion, StartedAt, LastSeenAt, OwnerUserId)
            VALUES (@Id, @Name, @Ver, @Start, @Last, @Owner)
            ON CONFLICT(SessionId) DO UPDATE SET
                ClientName = COALESCE(excluded.ClientName, Sessions.ClientName),
                ClientVersion = COALESCE(excluded.ClientVersion, Sessions.ClientVersion),
                LastSeenAt = excluded.LastSeenAt,
                OwnerUserId = COALESCE(Sessions.OwnerUserId, excluded.OwnerUserId);";
        cmd.Parameters.AddWithValue("@Id", session.SessionId);
        cmd.Parameters.AddWithValue("@Name", (object?)session.ClientName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Ver", (object?)session.ClientVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Start", session.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@Last", session.LastSeenAt.ToString("O"));
        cmd.Parameters.AddWithValue("@Owner", ResolveUserId(session.OwnerUserId));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<AuditSession[]> GetSessionsAsync(int limit = 50, string? ownerUserId = null)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        var where = string.IsNullOrWhiteSpace(ownerUserId) ? string.Empty : "WHERE OwnerUserId = @OwnerUserId";
        cmd.CommandText = $"SELECT SessionId, ClientName, ClientVersion, StartedAt, LastSeenAt, OwnerUserId FROM Sessions {where} ORDER BY LastSeenAt DESC LIMIT @Limit";
        cmd.Parameters.AddWithValue("@Limit", Math.Max(1, limit));
        if (!string.IsNullOrWhiteSpace(ownerUserId))
            cmd.Parameters.AddWithValue("@OwnerUserId", ownerUserId);

        var list = new List<AuditSession>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new AuditSession
            {
                SessionId = reader.GetString(0),
                ClientName = reader.IsDBNull(1) ? null : reader.GetString(1),
                ClientVersion = reader.IsDBNull(2) ? null : reader.GetString(2),
                StartedAt = DateTime.Parse(reader.GetString(3)),
                LastSeenAt = DateTime.Parse(reader.GetString(4)),
                OwnerUserId = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }

        return list.ToArray();
    }

    // —— 校验 ——

    public async Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(ct);

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Seq, Kind, RowId, RecordHash, PrevHash, Algorithm FROM AuditChain ORDER BY Seq";

            var prev = Genesis;
            long checkedCount = 0;
            long weakUnkeyed = 0;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var seq = reader.GetInt64(0);
                var kind = reader.GetString(1);
                var rowId = reader.GetInt64(2);
                var hash = reader.GetString(3);
                var prevHash = reader.GetString(4);
                var algorithm = reader.IsDBNull(5) ? null : reader.GetString(5);

                if (!string.Equals(prevHash, prev, StringComparison.Ordinal))
                    return Bad(seq, checkedCount, "PrevHash 链接不连续（审计记录被删除或插入）", weakUnkeyed);

                var payload = kind == "cmd"
                    ? await ReadCommandPayloadAsync(connection, null, rowId)
                    : await ReadSqlPayloadAsync(connection, null, rowId);

                if (payload is null)
                    return Bad(seq, checkedCount, "链引用的审计记录缺失", weakUnkeyed);

                string? expected;
                if (string.Equals(algorithm, AlgorithmSha256, StringComparison.OrdinalIgnoreCase))
                {
                    expected = ComputePlainHash(kind, rowId, payload, prev);
                    weakUnkeyed++;
                }
                else if (string.Equals(algorithm, AlgorithmHmac, StringComparison.OrdinalIgnoreCase))
                {
                    if (_signingKey is not { Length: > 0 })
                        return Bad(seq, checkedCount, "缺少签名密钥 audit.db.key，无法校验 HMAC 记录（不能据此判定被篡改）", weakUnkeyed, missingKey: true);
                    expected = ComputeHmacHash(kind, rowId, payload, prev);
                }
                else if (string.IsNullOrEmpty(algorithm))
                {
                    // v6 及更早的链没有 Algorithm 列：先按 HMAC 验，失败再按纯 SHA-256 验。
                    // 后者说明是历史弱签名，内容未改但强度降级，单列警告而不是报“被篡改”。
                    if (_signingKey is { Length: > 0 })
                    {
                        var hmac = ComputeHmacHash(kind, rowId, payload, prev);
                        if (string.Equals(hmac, hash, StringComparison.OrdinalIgnoreCase))
                        {
                            expected = hmac;
                        }
                        else
                        {
                            var plain = ComputePlainHash(kind, rowId, payload, prev);
                            if (string.Equals(plain, hash, StringComparison.OrdinalIgnoreCase))
                            {
                                expected = plain;
                                weakUnkeyed++;
                            }
                            else
                            {
                                expected = hmac;   // 内容不匹配：按篡改处理
                            }
                        }
                    }
                    else
                    {
                        var plain = ComputePlainHash(kind, rowId, payload, prev);
                        if (string.Equals(plain, hash, StringComparison.OrdinalIgnoreCase))
                        {
                            expected = plain;
                            weakUnkeyed++;
                        }
                        else
                        {
                            return Bad(seq, checkedCount,
                                "缺少签名密钥 audit.db.key，无法校验该记录（不能据此判定被篡改）",
                                weakUnkeyed, missingKey: true);
                        }
                    }
                }
                else
                {
                    return Bad(seq, checkedCount, $"未知的签名算法：{algorithm}", weakUnkeyed);
                }

                if (!string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
                    return Bad(seq, checkedCount, "记录哈希不匹配（审计记录被篡改）", weakUnkeyed);

                prev = hash;
                checkedCount++;
            }

            var message = checkedCount == 0
                ? "链为空"
                : $"校验通过：{checkedCount} 条链记录完整且未被篡改";
            if (weakUnkeyed > 0)
                message += $"；其中 {weakUnkeyed} 条为历史无密钥 SHA-256 签名（强度降级，建议保留 audit.db.key）";
            return new AuditVerifyResult
            {
                Ok = true,
                Checked = checkedCount,
                WeakUnkeyed = weakUnkeyed,
                Message = message
            };
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // —— 内部实现 ——

    /// <summary>
    /// 删除归档表中早于 <paramref name="cutoffUtc"/> 的记录，并按剩余活动/归档记录重建哈希链。
    /// 仅由管理员在存储维护中显式触发；返回删除的记录数。
    /// </summary>
    public async Task<int> PruneHistoryAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = await EnterWriteAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(WriteConnectionString);
            await connection.OpenAsync(ct);
            await SetBusyTimeoutAsync(connection);

            var cutoff = cutoffUtc.ToUniversalTime().ToString("O");
            int removed;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "DELETE FROM AuditLogsHistory WHERE Timestamp < @c; " +
                    "DELETE FROM SqlAuditLogsHistory WHERE Timestamp < @c;";
                cmd.Parameters.AddWithValue("@c", cutoff);
                removed = await cmd.ExecuteNonQueryAsync(ct);
            }

            // 归档行删掉后原链会缺记录；按剩余记录重建，保证校验只覆盖保留区间且仍连续。
            if (removed > 0)
                RebuildChain(connection);
            return removed;
        }
        finally
        {
            ExitWrite(gate);
        }
    }

    private static AuditVerifyResult Bad(long seq, long checkedCount, string message,
        long weakUnkeyed = 0, bool missingKey = false) =>
        new()
        {
            Ok = false,
            Checked = checkedCount,
            FirstBadSeq = seq,
            WeakUnkeyed = weakUnkeyed,
            MissingKey = missingKey,
            Message = message
        };

    /// <summary>把超过保留天数的活动记录移动到历史归档表（永久保留），链不删除、记录 Id 不变。</summary>
    private void ArchiveExpired(SqliteConnection connection)
    {
        var retentionDays = _securityOptions?.Audit.RetentionDays ?? 0;
        if (retentionDays <= 0)
            return;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("O");
        using var tx = connection.BeginTransaction();

        MoveExpired(connection, tx, "AuditLogs", "AuditLogsHistory", cutoff);
        MoveExpired(connection, tx, "SqlAuditLogs", "SqlAuditLogsHistory", cutoff);

        tx.Commit();
    }

    private static void MoveExpired(SqliteConnection connection, SqliteTransaction tx, string live, string history, string cutoff)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT OR IGNORE INTO {history} SELECT * FROM {live} WHERE Timestamp < @Cutoff; " +
                          $"DELETE FROM {live} WHERE Timestamp < @Cutoff;";
        cmd.Parameters.AddWithValue("@Cutoff", cutoff);
        cmd.ExecuteNonQuery();
    }

    /// <summary>若表缺少某列则 ALTER TABLE 补上（用于旧库平滑升级）。</summary>
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string type)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// 审计格式版本升级入口（任意旧版本 → v7）：
    /// <list type="bullet">
    /// <item>v6 → v7：只补 <c>Algorithm</c> 列并回填每环算法，<b>不重建链</b>，现有 RecordHash/Seq 全部保留；</item>
    /// <item>v5 及更早：沿用原迁移策略——补齐缺失列后按当前 payload 重建哈希链（链从 Genesis 重新开始，历史记录不丢）；</item>
    /// <item>旧记录缺失的归因/分类列保持 NULL（UI 按既有规则显示为空或 local）。</item>
    /// </list>
    /// </summary>
    private void UpgradeFormat(SqliteConnection connection)
    {
        int version;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version";
            version = Convert.ToInt32(read.ExecuteScalar() ?? 0);
        }

        if (version >= AuditFormatVersion)
            return;

        if (version < ChainRebuildVersion)
        {
            RebuildChain(connection);
            return;
        }

        BackfillChainAlgorithms(connection);
    }

    /// <summary>v6 → v7：保留原链，按现有 RecordHash 反推每环使用的算法并回填。</summary>
    private void BackfillChainAlgorithms(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();

        var rows = new List<(long Seq, string Kind, long RowId, string RecordHash, string PrevHash)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT Seq, Kind, RowId, RecordHash, PrevHash FROM AuditChain ORDER BY Seq;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
                    reader.GetString(3), reader.GetString(4)));
            }
        }

        foreach (var row in rows)
        {
            var payload = row.Kind == "cmd"
                ? ReadCommandPayload(connection, tx, row.RowId)
                : ReadSqlPayload(connection, tx, row.RowId);
            if (payload is null)
                continue;

            var algorithm = DetectAlgorithm(row.Kind, row.RowId, payload, row.PrevHash, row.RecordHash);
            if (algorithm is null)
                continue;

            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE AuditChain SET Algorithm = @Algorithm WHERE Seq = @Seq;";
            update.Parameters.AddWithValue("@Algorithm", algorithm);
            update.Parameters.AddWithValue("@Seq", row.Seq);
            update.ExecuteNonQuery();
        }

        StampFormatVersion(connection, tx);
        tx.Commit();
    }

    /// <summary>按历史 RecordHash 判断当时使用的算法；两者都不匹配时返回 null（保留待校验时报告）。</summary>
    private string? DetectAlgorithm(string kind, long rowId, string payload, string prev, string recordHash)
    {
        if (_signingKey is { Length: > 0 })
        {
            var hmac = ComputeHmacHash(kind, rowId, payload, prev);
            if (string.Equals(hmac, recordHash, StringComparison.OrdinalIgnoreCase))
                return AlgorithmHmac;
        }

        var plain = ComputePlainHash(kind, rowId, payload, prev);
        return string.Equals(plain, recordHash, StringComparison.OrdinalIgnoreCase) ? AlgorithmSha256 : null;
    }

    /// <summary>v5 及更早 → v7：保留记录，按当前 payload 重建哈希链（链首重新从 Genesis 开始）。</summary>
    private void RebuildChain(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();

        // 重建链前直接重建 AuditChain：既清空旧链，也兼容极老版本可能不同的表结构。
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = @"
                DROP TABLE IF EXISTS AuditChain;
                CREATE TABLE AuditChain (
                    Seq INTEGER PRIMARY KEY AUTOINCREMENT,
                    Kind TEXT NOT NULL,
                    RowId INTEGER NOT NULL,
                    RecordHash TEXT NOT NULL,
                    PrevHash TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    Algorithm TEXT
                );";
            clear.ExecuteNonQuery();
        }

        var rows = new List<(string Kind, long Id, string Timestamp)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText =
                "SELECT 'cmd' AS Kind, Id, Timestamp FROM AuditLogs " +
                "UNION ALL SELECT 'cmd', Id, Timestamp FROM AuditLogsHistory " +
                "UNION ALL SELECT 'sql', Id, Timestamp FROM SqlAuditLogs " +
                "UNION ALL SELECT 'sql', Id, Timestamp FROM SqlAuditLogsHistory " +
                "ORDER BY Timestamp, Id;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        }

        var prev = Genesis;
        var algorithm = CurrentAlgorithm;
        var seen = new HashSet<(string, long)>();
        foreach (var (kind, id, timestamp) in rows)
        {
            if (!seen.Add((kind, id)))
                continue;
            var payload = kind == "cmd"
                ? ReadCommandPayload(connection, tx, id)
                : ReadSqlPayload(connection, tx, id);
            if (payload is null)
                continue;

            var hash = ComputeHash(kind, id, payload, prev);
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO AuditChain (Kind, RowId, RecordHash, PrevHash, CreatedAt, Algorithm) VALUES (@Kind, @RowId, @Hash, @Prev, @CreatedAt, @Algorithm)";
            insert.Parameters.AddWithValue("@Kind", kind);
            insert.Parameters.AddWithValue("@RowId", id);
            insert.Parameters.AddWithValue("@Hash", hash);
            insert.Parameters.AddWithValue("@Prev", prev);
            insert.Parameters.AddWithValue("@CreatedAt", timestamp);
            insert.Parameters.AddWithValue("@Algorithm", algorithm);
            insert.ExecuteNonQuery();
            prev = hash;
        }

        StampFormatVersion(connection, tx);
        tx.Commit();
    }

    private static void StampFormatVersion(SqliteConnection connection, SqliteTransaction tx)
    {
        using var stamp = connection.CreateCommand();
        stamp.Transaction = tx;
        stamp.CommandText = $"PRAGMA user_version = {AuditFormatVersion};";
        stamp.ExecuteNonQuery();
    }

    private const string CommandPayloadColumns =
        "ServerId, ServerName, Command, Result, Status, Timestamp, ExitCode, IsFileTransfer, FilePath, FileSize, SessionId, Tool, Category, Decision, UserId";

    private const string SqlPayloadColumns =
        "DataSourceId, DataSourceName, Operation, Sql, Status, Result, RowsAffected, DurationMs, Timestamp, SessionId, Tool, Category, Decision, UserId";

    private static string? ReadCommandPayload(SqliteConnection connection, SqliteTransaction? tx, long id)
        => ReadPayload(connection, tx, "AuditLogs", "AuditLogsHistory", CommandPayloadColumns, id);

    private static string? ReadSqlPayload(SqliteConnection connection, SqliteTransaction? tx, long id)
        => ReadPayload(connection, tx, "SqlAuditLogs", "SqlAuditLogsHistory", SqlPayloadColumns, id);

    private static string? ReadPayload(SqliteConnection connection, SqliteTransaction? tx,
        string liveTable, string historyTable, string columns, long id)
    {
        var live = ReadPayloadFrom(connection, tx, liveTable, columns, id);
        return live ?? ReadPayloadFrom(connection, tx, historyTable, columns, id);
    }

    private static string? ReadPayloadFrom(SqliteConnection connection, SqliteTransaction? tx,
        string table, string columns, long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {columns} FROM {table} WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var fields = new List<object?>();
        for (var i = 0; i < reader.FieldCount; i++)
            fields.Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
        return BuildPayload(fields.ToArray());
    }

    private async Task AppendChainAsync(SqliteConnection connection, SqliteTransaction tx, string kind, long rowId, string payload)
    {
        string prev;
        await using (var readPrev = connection.CreateCommand())
        {
            readPrev.Transaction = tx;
            readPrev.CommandText = "SELECT RecordHash FROM AuditChain ORDER BY Seq DESC LIMIT 1";
            prev = (await readPrev.ExecuteScalarAsync()) as string ?? Genesis;
        }

        var hash = ComputeHash(kind, rowId, payload, prev);
        var algorithm = CurrentAlgorithm;

        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO AuditChain (Kind, RowId, RecordHash, PrevHash, CreatedAt, Algorithm) VALUES (@Kind, @RowId, @Hash, @Prev, @CreatedAt, @Algorithm)";
        insert.Parameters.AddWithValue("@Kind", kind);
        insert.Parameters.AddWithValue("@RowId", rowId);
        insert.Parameters.AddWithValue("@Hash", hash);
        insert.Parameters.AddWithValue("@Prev", prev);
        insert.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("@Algorithm", algorithm);
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<string?> ReadCommandPayloadAsync(SqliteConnection connection, SqliteTransaction? tx, long id)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT ServerId, ServerName, Command, Result, Status, Timestamp, ExitCode, IsFileTransfer, FilePath, FileSize, SessionId, Tool, Category, Decision, UserId FROM AuditLogs WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        if (await ReadPayloadAsync(cmd, commandSource: true) is { } live)
            return live;

        cmd.CommandText = "SELECT ServerId, ServerName, Command, Result, Status, Timestamp, ExitCode, IsFileTransfer, FilePath, FileSize, SessionId, Tool, Category, Decision, UserId FROM AuditLogsHistory WHERE Id = @Id";
        cmd.Parameters["@Id"].Value = id;
        return await ReadPayloadAsync(cmd, commandSource: true);
    }

    private async Task<string?> ReadSqlPayloadAsync(SqliteConnection connection, SqliteTransaction? tx, long id)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT DataSourceId, DataSourceName, Operation, Sql, Status, Result, RowsAffected, DurationMs, Timestamp, SessionId, Tool, Category, Decision, UserId FROM SqlAuditLogs WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        if (await ReadPayloadAsync(cmd, commandSource: false) is { } live)
            return live;

        cmd.CommandText = "SELECT DataSourceId, DataSourceName, Operation, Sql, Status, Result, RowsAffected, DurationMs, Timestamp, SessionId, Tool, Category, Decision, UserId FROM SqlAuditLogsHistory WHERE Id = @Id";
        cmd.Parameters["@Id"].Value = id;
        return await ReadPayloadAsync(cmd, commandSource: false);
    }

    private static async Task<string?> ReadPayloadAsync(SqliteCommand cmd, bool commandSource)
    {
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        var fields = new List<object?>();
        for (var i = 0; i < reader.FieldCount; i++)
            fields.Add(reader.IsDBNull(i) ? null : reader.GetValue(i));

        return BuildPayload(fields.ToArray());
    }

    /// <summary>长度前缀拼接，避免字段内含分隔符造成歧义。</summary>
    private static string BuildPayload(object?[] fields)
    {
        var sb = new StringBuilder();
        foreach (var field in fields)
        {
            var text = field switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => field.ToString() ?? string.Empty
            };
            sb.Append(text.Length).Append(':').Append(text).Append('|');
        }

        return sb.ToString();
    }

    private string CurrentAlgorithm => _signingKey is { Length: > 0 } ? AlgorithmHmac : AlgorithmSha256;

    private string ComputeHash(string kind, long rowId, string payload, string prev)
        => _signingKey is { Length: > 0 }
            ? ComputeHmacHash(kind, rowId, payload, prev)
            : ComputePlainHash(kind, rowId, payload, prev);

    private string ComputeHmacHash(string kind, long rowId, string payload, string prev)
    {
        var data = Encoding.UTF8.GetBytes($"{kind}|{rowId}|{payload}|{prev}");
        using var hmac = new HMACSHA256(_signingKey!);
        return Convert.ToHexString(hmac.ComputeHash(data));
    }

    private static string ComputePlainHash(string kind, long rowId, string payload, string prev)
    {
        var data = Encoding.UTF8.GetBytes($"{kind}|{rowId}|{payload}|{prev}");
        return Convert.ToHexString(SHA256.HashData(data));
    }

    private byte[]? LoadOrCreateSigningKey(SqliteConnection connection)
    {
        var keyPath = _dbPath + ".key";
        try
        {
            var protector = new DpapiSecretProtector();
            if (File.Exists(keyPath))
            {
                var encoded = protector.Unprotect(File.ReadAllText(keyPath));
                return string.IsNullOrEmpty(encoded) ? null : Convert.FromBase64String(encoded);
            }

            // 密钥文件丢失时不能静默轮换：若链里已有 HMAC 记录（或无法判断），
            // 新建密钥会让历史链全部被误判为篡改；此时保持无密钥，校验时明确提示“无法校验”。
            if (ChainNeedsExistingKey(connection))
                return null;

            var key = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllText(keyPath, protector.Protect(Convert.ToBase64String(key)));
            return key;
        }
        catch
        {
            // 无法建立签名密钥时退化为纯 SHA-256（仍可检测常规篡改）
            return null;
        }
    }

    /// <summary>链中是否存在必须依赖旧密钥才能验证的记录（HMAC 或无法用纯 SHA-256 复算的行）。</summary>
    private bool ChainNeedsExistingKey(SqliteConnection connection)
    {
        try
        {
            var rows = new List<(string Kind, long RowId, string RecordHash, string PrevHash, string? Algorithm)>();
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT Kind, RowId, RecordHash, PrevHash, Algorithm FROM AuditChain ORDER BY Seq;";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                        reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
                }
            }

            foreach (var row in rows)
            {
                if (string.Equals(row.Algorithm, AlgorithmHmac, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(row.Algorithm, AlgorithmSha256, StringComparison.OrdinalIgnoreCase))
                    continue;

                var payload = row.Kind == "cmd"
                    ? ReadCommandPayload(connection, null, row.RowId)
                    : ReadSqlPayload(connection, null, row.RowId);
                if (payload is null)
                    return true;

                var plain = ComputePlainHash(row.Kind, row.RowId, payload, row.PrevHash);
                if (!string.Equals(plain, row.RecordHash, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch
        {
            // 无法判断时保守处理：不创建新密钥。
            return true;
        }
    }

    private static string AppendRedactionMarker(string? result)
    {
        const string marker = "[敏感内容已自动脱敏]";
        if (string.IsNullOrEmpty(result))
            return marker;
        return result.Contains(marker, StringComparison.Ordinal) ? result : result + "\n" + marker;
    }

    private static CommandAuditLog ReadCommandRow(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ServerId = reader.GetString(1),
        ServerName = reader.GetString(2),
        Command = reader.GetString(3),
        Result = reader.IsDBNull(4) ? null : reader.GetString(4),
        Status = Enum.Parse<CommandStatus>(reader.GetString(5)),
        Timestamp = DateTime.Parse(reader.GetString(6)),
        ExitCode = reader.IsDBNull(7) ? null : reader.GetInt32(7),
        IsFileTransfer = reader.GetInt32(8) == 1,
        FilePath = reader.IsDBNull(9) ? null : reader.GetString(9),
        FileSize = reader.IsDBNull(10) ? null : reader.GetInt64(10),
        SessionId = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null,
        Tool = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null,
        Category = ReadCategory(reader),
        Decision = reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : null,
        UserId = reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetString(15) : null
    };

    /// <summary>读取 Category；缺失(旧库)时按 IsFileTransfer 回退。</summary>
    private static AuditCategory ReadCategory(SqliteDataReader reader)
    {
        if (reader.FieldCount > 13 && !reader.IsDBNull(13) &&
            Enum.TryParse<AuditCategory>(reader.GetString(13), ignoreCase: true, out var category))
            return category;

        return reader.GetInt32(8) == 1 ? AuditCategory.Transfer : AuditCategory.Exec;
    }

    /// <summary>写入用的事件类型名：文件传输显式落到 Transfer。</summary>
    private static string CategoryName(CommandAuditLog log) =>
        log.IsFileTransfer && log.Category == AuditCategory.Exec ? AuditCategory.Transfer.ToString() : log.Category.ToString();

    private static SqlAuditLog ReadSqlRow(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        DataSourceId = reader.GetString(1),
        DataSourceName = reader.GetString(2),
        Operation = Enum.Parse<SqlOperation>(reader.GetString(3)),
        Sql = reader.GetString(4),
        Status = Enum.Parse<CommandStatus>(reader.GetString(5)),
        Result = reader.IsDBNull(6) ? null : reader.GetString(6),
        RowsAffected = reader.IsDBNull(7) ? null : reader.GetInt64(7),
        DurationMs = reader.IsDBNull(8) ? null : reader.GetDouble(8),
        Timestamp = DateTime.Parse(reader.GetString(9)),
        SessionId = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetString(10) : null,
        Tool = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null,
        Category = reader.FieldCount > 12 && !reader.IsDBNull(12) &&
                   Enum.TryParse<AuditCategory>(reader.GetString(12), ignoreCase: true, out var cat) ? cat : AuditCategory.Exec,
        Decision = reader.FieldCount > 13 && !reader.IsDBNull(13) ? reader.GetString(13) : null,
        UserId = reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : null
    };

    /// <summary>
    /// 归因用户：显式指定 &gt; 宿主注入的当前上下文 &gt; "local"（桌面未登录，评估报告 D1）。
    /// 本方法不接受工具/前端传入——调用方只能传宿主注入或上下文值，防伪造（§10.A.5）。
    /// </summary>
    private static string ResolveUserId(string? explicitUserId) =>
        !string.IsNullOrWhiteSpace(explicitUserId) ? explicitUserId! :
        !string.IsNullOrWhiteSpace(AuditContext.CurrentUserId) ? AuditContext.CurrentUserId! :
        "local";
}
