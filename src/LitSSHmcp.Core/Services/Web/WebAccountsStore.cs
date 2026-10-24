using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Core.Services.Web;

/// <summary>Web 用户行（含登录锁定状态；哈希/盐字段仅供校验，禁止外传）。</summary>
public sealed record WebUserRow(
    long Id,
    string Username,
    string PasswordHash,
    string Salt,
    int Iterations,
    string Role,
    int FailedCount,
    DateTime? LockedUntilUtc,
    DateTime? LastLoginAtUtc);

/// <summary>Web 登录令牌行（TokenHash 为令牌的 SHA-256，明文令牌只在发放时返回一次）。</summary>
public sealed record WebTokenRow(long Id, long UserId, DateTime CreatedAtUtc, DateTime ExpiresAtUtc);

/// <summary>
/// Web 账号库（%APPDATA%\LitSSH\web.db）：自建账号体系（评估报告 §3.2 M3-1）。
/// 只有 Core 层数据访问与密码哈希策略；HTTP/会话由 LitSSHmcp.Web 宿主负责。
/// 哈希参数复用 appLock 先例（PBKDF2-SHA256 210k / salt 16B / hash 32B）。
/// </summary>
public sealed class WebAccountsStore
{
    private readonly string _dbPath;

    public WebAccountsStore(string? dbPath = null)
    {
        _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.WebDb : dbPath!;
    }

    /// <summary>建库建表（幂等）。WAL + busy_timeout 与 agent.db 先例一致。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_dbPath))!);
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS web_users(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Username TEXT NOT NULL COLLATE NOCASE,
                    PasswordHash TEXT NOT NULL,
                    Salt TEXT NOT NULL,
                    Iterations INTEGER NOT NULL,
                    Role TEXT NOT NULL DEFAULT 'user',
                    FailedCount INTEGER NOT NULL DEFAULT 0,
                    LockedUntilUtc TEXT,
                    LastLoginAtUtc TEXT,
                    UNIQUE(Username)
                );
                CREATE TABLE IF NOT EXISTS web_tokens(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TokenHash TEXT NOT NULL UNIQUE,
                    UserId INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    ExpiresAtUtc TEXT NOT NULL,
                    FOREIGN KEY(UserId) REFERENCES web_users(Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_web_tokens_user ON web_tokens(UserId);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>是否已有任何账号（宿主首个账号走管理员引导）。</summary>
    public async Task<bool> HasAnyUserAsync(CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM web_users)";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) != 0;
    }

    /// <summary>创建账号。用户名 3-32 位字母数字/_/-/.；密码最短 <see cref="WebAuthPolicy.MinPasswordLength"/>；角色 admin|user。</summary>
    /// <returns>新账号 Id。</returns>
    /// <exception cref="ArgumentException">用户名/密码/角色非法。</exception>
    /// <exception cref="InvalidOperationException">用户名已存在。</exception>
    public async Task<long> CreateAsync(string username, string password, string role = "user", int? iterations = null, CancellationToken ct = default)
    {
        username = (username ?? string.Empty).Trim();
        if (!WebAuthPolicy.IsValidUsername(username))
            throw new ArgumentException("用户名须为 3-32 位字母数字/_/-/.", nameof(username));
        if (string.IsNullOrEmpty(password) || password.Length < WebAuthPolicy.MinPasswordLength)
            throw new ArgumentException($"密码至少 {WebAuthPolicy.MinPasswordLength} 位", nameof(password));
        role = (role ?? string.Empty).Trim().ToLowerInvariant();
        if (role is not ("admin" or "user"))
            throw new ArgumentException("角色须为 admin 或 user", nameof(role));

        var iters = iterations is > 0 ? iterations.Value : WebAuthPolicy.DefaultIterations;
        var salt = RandomNumberGenerator.GetBytes(WebAuthPolicy.SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, WebAuthPolicy.HashSize);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO web_users(Username, PasswordHash, Salt, Iterations, Role)
            VALUES ($u, $h, $s, $i, $r);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", Convert.ToBase64String(hash));
        cmd.Parameters.AddWithValue("$s", Convert.ToBase64String(salt));
        cmd.Parameters.AddWithValue("$i", iters);
        cmd.Parameters.AddWithValue("$r", role);
        try
        {
            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            return id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)   // UNIQUE 约束
        {
            throw new InvalidOperationException("用户名已存在", ex);
        }
    }

    /// <summary>按用户名查账号（大小写不敏感）。</summary>
    public async Task<WebUserRow?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Username, PasswordHash, Salt, Iterations, Role, FailedCount, LockedUntilUtc, LastLoginAtUtc FROM web_users WHERE Username = $u COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$u", (username ?? string.Empty).Trim());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadUser(r) : null;
    }

    /// <summary>按 Id 查账号。</summary>
    public async Task<WebUserRow?> FindByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Username, PasswordHash, Salt, Iterations, Role, FailedCount, LockedUntilUtc, LastLoginAtUtc FROM web_users WHERE Id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadUser(r) : null;
    }

    /// <summary>列出全部账号（含锁定状态；供桌面管理界面）。</summary>
    public async Task<IReadOnlyList<WebUserRow>> ListUsersAsync(CancellationToken ct = default)
    {
        var list = new List<WebUserRow>();
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Username, PasswordHash, Salt, Iterations, Role, FailedCount, LockedUntilUtc, LastLoginAtUtc FROM web_users ORDER BY Id";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadUser(r));
        return list;
    }

    /// <summary>登录成功：清零失败计数与锁定，回写 LastLoginAtUtc。</summary>
    public async Task RecordLoginSuccessAsync(long id, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE web_users SET FailedCount = 0, LockedUntilUtc = NULL, LastLoginAtUtc = $t WHERE Id = $i";
        cmd.Parameters.AddWithValue("$t", nowUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$i", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>登录失败：失败计数 +1，返回新计数。</summary>
    public async Task<int> RecordLoginFailedAsync(long id, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE web_users SET FailedCount = FailedCount + 1 WHERE Id = $i";
            cmd.Parameters.AddWithValue("$i", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using var q = conn.CreateCommand();
        q.CommandText = "SELECT FailedCount FROM web_users WHERE Id = $i";
        q.Parameters.AddWithValue("$i", id);
        var result = await q.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    /// <summary>设置/清除锁定到期时间（UTC；null 解锁）。</summary>
    public async Task SetLockoutAsync(long id, DateTime? lockedUntilUtc, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE web_users SET LockedUntilUtc = $t WHERE Id = $i";
        cmd.Parameters.AddWithValue("$t", lockedUntilUtc is null ? DBNull.Value : lockedUntilUtc.Value.ToString("O"));
        cmd.Parameters.AddWithValue("$i", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>清零失败计数并解除锁定（管理员重置密码后调用；不改动 LastLoginAtUtc）。</summary>
    public async Task ResetLoginFailuresAsync(long id, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE web_users SET FailedCount = 0, LockedUntilUtc = NULL WHERE Id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>发放登录令牌（只返回明文一次；落库存 SHA-256）。</summary>
    public async Task<(string Token, DateTime ExpiresAtUtc)> CreateTokenAsync(long userId, DateTime nowUtc, TimeSpan lifetime, CancellationToken ct = default)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var token = WebAuthPolicy.ToBase64Url(raw);
        var expires = nowUtc + lifetime;
        var hash = HashToken(token);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO web_tokens(TokenHash, UserId, CreatedAtUtc, ExpiresAtUtc) VALUES ($h, $u, $c, $e)";
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$c", nowUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$e", expires.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        return (token, expires);
    }

    /// <summary>校验令牌：命中且未过期返回行；过期顺手删除；未命中/已删返回 null。</summary>
    public async Task<WebTokenRow?> ValidateTokenAsync(string? token, DateTime nowUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = HashToken(token!);
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, UserId, CreatedAtUtc, ExpiresAtUtc FROM web_tokens WHERE TokenHash = $h";
            cmd.Parameters.AddWithValue("$h", hash);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            var row = new WebTokenRow(r.GetInt64(0), r.GetInt64(1), ParseUtc(r.GetString(2)), ParseUtc(r.GetString(3)));
            if (row.ExpiresAtUtc <= nowUtc)
            {
                await r.DisposeAsync();
                await using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM web_tokens WHERE TokenHash = $h";
                del.Parameters.AddWithValue("$h", hash);
                await del.ExecuteNonQueryAsync(ct);
                return null;
            }
            return row;
        }
    }

    /// <summary>吊销单个令牌（登出）。</summary>
    public async Task RevokeTokenAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM web_tokens WHERE TokenHash = $h";
        cmd.Parameters.AddWithValue("$h", HashToken(token!));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>吊销用户全部令牌（改密码/禁用账号时调用）。</summary>
    public async Task RevokeAllTokensAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM web_tokens WHERE UserId = $u";
        cmd.Parameters.AddWithValue("$u", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>修改密码（重哈希，不触碰锁定状态；调用方应随后吊销全部令牌）。</summary>
    public async Task ChangePasswordAsync(long id, string newPassword, int? iterations = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < WebAuthPolicy.MinPasswordLength)
            throw new ArgumentException($"密码至少 {WebAuthPolicy.MinPasswordLength} 位", nameof(newPassword));
        var iters = iterations is > 0 ? iterations.Value : WebAuthPolicy.DefaultIterations;
        var salt = RandomNumberGenerator.GetBytes(WebAuthPolicy.SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(newPassword, salt, iters, HashAlgorithmName.SHA256, WebAuthPolicy.HashSize);
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE web_users SET PasswordHash = $h, Salt = $s, Iterations = $i WHERE Id = $id";
        cmd.Parameters.AddWithValue("$h", Convert.ToBase64String(hash));
        cmd.Parameters.AddWithValue("$s", Convert.ToBase64String(salt));
        cmd.Parameters.AddWithValue("$i", iters);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>删除账号（级联删令牌）。</summary>
    public async Task<bool> DeleteUserAsync(long id, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM web_users WHERE Id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>修改角色。</summary>
    public async Task SetRoleAsync(long id, string role, CancellationToken ct = default)
    {
        role = (role ?? string.Empty).Trim().ToLowerInvariant();
        if (role is not ("admin" or "user"))
            throw new ArgumentException("角色须为 admin 或 user", nameof(role));
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE web_users SET Role = $r WHERE Id = $i";
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$i", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>常数时间校验密码（配置损坏按失败处理，不抛）。</summary>
    public static bool VerifyPassword(WebUserRow user, string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(user.Salt);
            expected = Convert.FromBase64String(user.PasswordHash);
        }
        catch
        {
            return false;
        }
        if (salt.Length == 0 || expected.Length == 0) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, user.Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string HashToken(string token)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static DateTime ParseUtc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static WebUserRow ReadUser(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.GetInt32(4),
        r.GetString(5),
        r.GetInt32(6),
        r.IsDBNull(7) ? null : ParseUtc(r.GetString(7)),
        r.IsDBNull(8) ? null : ParseUtc(r.GetString(8)));

    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
}
