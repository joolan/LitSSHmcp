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

    private readonly string _dbPath;
    private readonly ISecurityOptionsProvider? _securityOptions;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private byte[]? _signingKey;   // 为 null 时退化为纯 SHA-256

    public AuditLogService() : this(null, null)
    {
    }

    public AuditLogService(ISecurityOptionsProvider? securityOptions) : this(securityOptions, null)
    {
    }

    public AuditLogService(ISecurityOptionsProvider? securityOptions, string? dbPath)
    {
        _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.AuditDb : dbPath!;
        _securityOptions = securityOptions;
    }

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "PRAGMA busy_timeout=3000;";
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
                FileSize INTEGER
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
                Timestamp TEXT NOT NULL
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
                FileSize INTEGER
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
                Timestamp TEXT NOT NULL
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
                CreatedAt TEXT NOT NULL
            )";
        await cmd.ExecuteNonQueryAsync();

        _signingKey = LoadOrCreateSigningKey();

        ArchiveExpired(connection);
    }

    // —— 写入（活动表 + 链，同一事务） ——

    public async Task LogCommandAsync(CommandAuditLog log)
    {
        await _writeGate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            long id;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"
                    INSERT INTO AuditLogs (ServerId, ServerName, Command, Result, Status, Timestamp, ExitCode, IsFileTransfer, FilePath, FileSize)
                    VALUES (@ServerId, @ServerName, @Command, @Result, @Status, @Timestamp, @ExitCode, @IsFileTransfer, @FilePath, @FileSize);
                    SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("@ServerId", log.ServerId);
                insert.Parameters.AddWithValue("@ServerName", log.ServerName);
                insert.Parameters.AddWithValue("@Command", log.Command);
                insert.Parameters.AddWithValue("@Result", log.Result ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@Status", log.Status.ToString());
                insert.Parameters.AddWithValue("@Timestamp", log.Timestamp.ToString("O"));
                insert.Parameters.AddWithValue("@ExitCode", log.ExitCode ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@IsFileTransfer", log.IsFileTransfer ? 1 : 0);
                insert.Parameters.AddWithValue("@FilePath", log.FilePath ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@FileSize", log.FileSize ?? (object)DBNull.Value);
                id = (long)(await insert.ExecuteScalarAsync())!;
            }

            var payload = await ReadCommandPayloadAsync(connection, tx, id);
            await AppendChainAsync(connection, tx, "cmd", id, payload ?? string.Empty);
            tx.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task LogSqlAsync(SqlAuditLog log)
    {
        await _writeGate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            long id;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"
                    INSERT INTO SqlAuditLogs (DataSourceId, DataSourceName, Operation, Sql, Status, Result, RowsAffected, DurationMs, Timestamp)
                    VALUES (@DataSourceId, @DataSourceName, @Operation, @Sql, @Status, @Result, @RowsAffected, @DurationMs, @Timestamp);
                    SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("@DataSourceId", log.DataSourceId);
                insert.Parameters.AddWithValue("@DataSourceName", log.DataSourceName);
                insert.Parameters.AddWithValue("@Operation", log.Operation.ToString());
                insert.Parameters.AddWithValue("@Sql", log.Sql);
                insert.Parameters.AddWithValue("@Status", log.Status.ToString());
                insert.Parameters.AddWithValue("@Result", log.Result ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@RowsAffected", log.RowsAffected ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@DurationMs", log.DurationMs ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@Timestamp", log.Timestamp.ToString("O"));
                id = (long)(await insert.ExecuteScalarAsync())!;
            }

            var payload = await ReadSqlPayloadAsync(connection, tx, id);
            await AppendChainAsync(connection, tx, "sql", id, payload ?? string.Empty);
            tx.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // —— 查询（活动表 / 含历史归档） ——

    public async Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100, string? keyword = null, bool includeHistory = false)
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
            conditions.Add("Command LIKE @Keyword");
            cmd.Parameters.AddWithValue("@Keyword", $"%{keyword.Trim()}%");
        }

        var source = includeHistory
            ? "(SELECT * FROM AuditLogs UNION ALL SELECT * FROM AuditLogsHistory)"
            : "AuditLogs";
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        cmd.CommandText = $"SELECT * FROM {source} {where} ORDER BY Timestamp DESC LIMIT @Limit";
        cmd.Parameters.AddWithValue("@Limit", limit);

        var logs = new List<CommandAuditLog>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            logs.Add(ReadCommandRow(reader));

        return logs.ToArray();
    }

    public async Task<SqlAuditLog[]> GetSqlLogsAsync(string? dataSourceId = null, int limit = 100, string? keyword = null, bool includeHistory = false)
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
            conditions.Add("Sql LIKE @Keyword");
            cmd.Parameters.AddWithValue("@Keyword", $"%{keyword.Trim()}%");
        }

        var source = includeHistory
            ? "(SELECT * FROM SqlAuditLogs UNION ALL SELECT * FROM SqlAuditLogsHistory)"
            : "SqlAuditLogs";
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        cmd.CommandText = $"SELECT * FROM {source} {where} ORDER BY Timestamp DESC LIMIT @Limit";
        cmd.Parameters.AddWithValue("@Limit", limit);

        var logs = new List<SqlAuditLog>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            logs.Add(ReadSqlRow(reader));

        return logs.ToArray();
    }

    // —— 校验 ——

    public async Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(ct);

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Seq, Kind, RowId, RecordHash, PrevHash FROM AuditChain ORDER BY Seq";

            var prev = Genesis;
            long checkedCount = 0;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var seq = reader.GetInt64(0);
                var kind = reader.GetString(1);
                var rowId = reader.GetInt64(2);
                var hash = reader.GetString(3);
                var prevHash = reader.GetString(4);

                if (!string.Equals(prevHash, prev, StringComparison.Ordinal))
                    return Bad(seq, checkedCount, "PrevHash 链接不连续（审计记录被删除或插入）");

                var payload = kind == "cmd"
                    ? await ReadCommandPayloadAsync(connection, null, rowId)
                    : await ReadSqlPayloadAsync(connection, null, rowId);

                if (payload is null)
                    return Bad(seq, checkedCount, "链引用的审计记录缺失");

                var expected = ComputeHash(kind, rowId, payload, prev);
                if (!string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
                    return Bad(seq, checkedCount, "记录哈希不匹配（审计记录被篡改）");

                prev = hash;
                checkedCount++;
            }

            return new AuditVerifyResult
            {
                Ok = true,
                Checked = checkedCount,
                Message = checkedCount == 0 ? "链为空" : $"校验通过：{checkedCount} 条链记录完整且未被篡改"
            };
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // —— 内部实现 ——

    private static AuditVerifyResult Bad(long seq, long checkedCount, string message) =>
        new() { Ok = false, Checked = checkedCount, FirstBadSeq = seq, Message = message };

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

        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO AuditChain (Kind, RowId, RecordHash, PrevHash, CreatedAt) VALUES (@Kind, @RowId, @Hash, @Prev, @CreatedAt)";
        insert.Parameters.AddWithValue("@Kind", kind);
        insert.Parameters.AddWithValue("@RowId", rowId);
        insert.Parameters.AddWithValue("@Hash", hash);
        insert.Parameters.AddWithValue("@Prev", prev);
        insert.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<string?> ReadCommandPayloadAsync(SqliteConnection connection, SqliteTransaction? tx, long id)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT ServerId, ServerName, Command, Result, Status, Timestamp, ExitCode, IsFileTransfer, FilePath, FileSize FROM AuditLogs WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        if (await ReadPayloadAsync(cmd, commandSource: true) is { } live)
            return live;

        cmd.CommandText = "SELECT ServerId, ServerName, Command, Result, Status, Timestamp, ExitCode, IsFileTransfer, FilePath, FileSize FROM AuditLogsHistory WHERE Id = @Id";
        cmd.Parameters["@Id"].Value = id;
        return await ReadPayloadAsync(cmd, commandSource: true);
    }

    private async Task<string?> ReadSqlPayloadAsync(SqliteConnection connection, SqliteTransaction? tx, long id)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT DataSourceId, DataSourceName, Operation, Sql, Status, Result, RowsAffected, DurationMs, Timestamp FROM SqlAuditLogs WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        if (await ReadPayloadAsync(cmd, commandSource: false) is { } live)
            return live;

        cmd.CommandText = "SELECT DataSourceId, DataSourceName, Operation, Sql, Status, Result, RowsAffected, DurationMs, Timestamp FROM SqlAuditLogsHistory WHERE Id = @Id";
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

    private string ComputeHash(string kind, long rowId, string payload, string prev)
    {
        var data = Encoding.UTF8.GetBytes($"{kind}|{rowId}|{payload}|{prev}");
        if (_signingKey is { Length: > 0 })
        {
            using var hmac = new HMACSHA256(_signingKey);
            return Convert.ToHexString(hmac.ComputeHash(data));
        }

        return Convert.ToHexString(SHA256.HashData(data));
    }

    private byte[]? LoadOrCreateSigningKey()
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
        FileSize = reader.IsDBNull(10) ? null : reader.GetInt64(10)
    };

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
        Timestamp = DateTime.Parse(reader.GetString(9))
    };
}
