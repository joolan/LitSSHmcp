using System.Security.Cryptography;

namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>
/// 局域网代理账号。只保存 PBKDF2-SHA256 派生结果，不保存明文密码。
/// HTTP 使用 Basic、SOCKS5 使用 RFC 1929，两者共用本账号表。
/// </summary>
public sealed class ProxyAccount
{
    public const int DefaultIterations = 210_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int Iterations { get; set; } = DefaultIterations;
    public bool Enabled { get; set; } = true;
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; set; }
    public long RequestCount { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }

    /// <summary>设置或重置密码；只写入盐和哈希。</summary>
    public void SetPassword(string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("密码不能为空。", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = HashPassword(password, salt, iterations);
        PasswordSalt = Convert.ToBase64String(salt);
        PasswordHash = Convert.ToBase64String(hash);
        Iterations = Math.Max(1000, iterations);
    }

    /// <summary>校验密码；账号禁用、盐/哈希损坏或密码错误都返回 false。</summary>
    public bool VerifyPassword(string password)
    {
        if (!Enabled || string.IsNullOrEmpty(password) ||
            string.IsNullOrEmpty(PasswordSalt) || string.IsNullOrEmpty(PasswordHash))
            return false;

        try
        {
            var salt = Convert.FromBase64String(PasswordSalt);
            var expected = Convert.FromBase64String(PasswordHash);
            var actual = HashPassword(password, salt, Math.Max(1000, Iterations));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    public ProxyAccount Clone() => (ProxyAccount)MemberwiseClone();

    private static byte[] HashPassword(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Math.Max(1000, iterations),
            HashAlgorithmName.SHA256, HashBytes);
}
