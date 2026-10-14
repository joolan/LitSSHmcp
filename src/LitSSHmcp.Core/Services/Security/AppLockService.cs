using System.Security.Cryptography;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 应用锁（启动密码门卫）服务：设置/修改/关闭密码、PBKDF2-SHA256 校验。
/// 哈希与盐经 <see cref="IConfigService"/> 的 DPAPI 通道落盘，明文密码绝不落盘。
/// 定位是「防他人随手拿到电脑直接操作」的门卫，不是凭据金库（凭据仍由 DPAPI(CurrentUser) 保护）。
/// </summary>
public class AppLockService
{
    /// <summary>密码最短长度。</summary>
    public const int MinPasswordLength = 6;

    private const int DefaultIterations = 210_000;   // OWASP 对 PBKDF2-SHA256 的建议
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private readonly IConfigService _configService;

    public AppLockService(IConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>应用锁是否已配置（启用且哈希/盐完整）。</summary>
    public static bool IsConfigured(AppLockConfig? lockConfig)
        => lockConfig is { Enabled: true }
           && !string.IsNullOrEmpty(lockConfig.PasswordHash)
           && !string.IsNullOrEmpty(lockConfig.Salt);

    /// <summary>当前是否已配置（读配置判断）。</summary>
    public async Task<bool> IsEnabledAsync()
    {
        var config = await _configService.LoadConfigAsync();
        return IsConfigured(config.AppLock);
    }

    /// <summary>设置/修改密码（首次设置直接启用）。密码过短抛 <see cref="ArgumentException"/>。</summary>
    public async Task SetPasswordAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinPasswordLength)
            throw new ArgumentException($"密码至少 {MinPasswordLength} 位", nameof(password));

        var config = await _configService.LoadConfigAsync();
        var iterations = config.AppLock?.Iterations is > 0 ? config.AppLock.Iterations : DefaultIterations;
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);

        config.AppLock = new AppLockConfig
        {
            Enabled = true,
            PasswordHash = Convert.ToBase64String(hash),
            Salt = Convert.ToBase64String(salt),
            Iterations = iterations
        };
        await _configService.SaveConfigAsync(config);
    }

    /// <summary>校验密码（常数时间比较）。未配置或输入为空返回 false。</summary>
    public async Task<bool> VerifyAsync(string password)
    {
        var config = await _configService.LoadConfigAsync();
        var lockConfig = config.AppLock;
        if (!IsConfigured(lockConfig) || string.IsNullOrEmpty(password))
            return false;

        byte[] salt, expected;
        var saltB64 = lockConfig!.Salt;
        var hashB64 = lockConfig.PasswordHash;
        if (string.IsNullOrEmpty(saltB64) || string.IsNullOrEmpty(hashB64))
            return false;
        try
        {
            salt = Convert.FromBase64String(saltB64);
            expected = Convert.FromBase64String(hashB64);
        }
        catch
        {
            return false;   // 配置损坏：按校验失败处理，不抛异常
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, lockConfig.Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>关闭应用锁并清空哈希/盐（调用方应先 <see cref="VerifyAsync"/> 验证旧密码）。</summary>
    public async Task DisableAsync()
    {
        var config = await _configService.LoadConfigAsync();
        config.AppLock = new AppLockConfig();
        await _configService.SaveConfigAsync(config);
    }
}
