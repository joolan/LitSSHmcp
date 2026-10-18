using System.Security.Cryptography;

namespace LitSSHmcp.Core.Services.Web;

/// <summary>登录结果状态。</summary>
public enum WebLoginStatus
{
    Success,
    InvalidCredentials,
    Locked
}

/// <summary>登录成功后的主体（授权矩阵/审计归因用；不含令牌）。</summary>
public sealed record WebPrincipal(long UserId, string Username, string Role)
{
    public bool IsAdmin => string.Equals(Role, "admin", StringComparison.OrdinalIgnoreCase);
}

/// <summary>登录结果（Success 才带明文 Token；Locked 带剩余锁定时间）。</summary>
public sealed record WebLoginOutcome(
    WebLoginStatus Status,
    string? Token = null,
    DateTime? ExpiresAtUtc = null,
    WebUserRow? User = null,
    TimeSpan? RetryAfter = null);

/// <summary>
/// Web 登录/令牌服务（评估报告 §3.2 M3-1 / §10.B.1）：
/// PBKDF2 校验 + 失败锁定（WebAuthPolicy 曲线，持久化到 web.db）+ 12h 令牌（库存 SHA-256）。
/// 时钟可注入（测试）；用户不存在时仍做一次哑哈希，缩小用户枚举时序差。
/// </summary>
public sealed class WebAuthService
{
    /// <summary>令牌有效期（绝对；登出/改密可提前吊销）。</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    private readonly WebAccountsStore _store;
    private readonly Func<DateTime> _utcNow;

    public WebAuthService(WebAccountsStore store, Func<DateTime>? utcNow = null)
    {
        _store = store;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
    }

    /// <summary>登录：校验凭据 → 成功发令牌并清锁定；失败累计计数，达阈值按曲线锁定。</summary>
    public async Task<WebLoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var now = _utcNow();
        var user = await _store.FindByUsernameAsync(username, ct);
        if (user is null)
        {
            DummyHash(password);   // 用户不存在也做一次 PBKDF2，防时序枚举
            return new WebLoginOutcome(WebLoginStatus.InvalidCredentials);
        }

        if (user.LockedUntilUtc is { } locked && locked > now)
            return new WebLoginOutcome(WebLoginStatus.Locked, User: user, RetryAfter: locked - now);

        if (!WebAccountsStore.VerifyPassword(user, password))
        {
            var fails = await _store.RecordLoginFailedAsync(user.Id, ct);
            var lockout = WebAuthPolicy.ComputeLockout(fails);
            if (lockout > TimeSpan.Zero)
            {
                var until = now + lockout;
                await _store.SetLockoutAsync(user.Id, until, ct);
                return new WebLoginOutcome(WebLoginStatus.Locked, User: user, RetryAfter: lockout);
            }
            return new WebLoginOutcome(WebLoginStatus.InvalidCredentials, User: user);
        }

        await _store.RecordLoginSuccessAsync(user.Id, now, ct);
        var (token, expires) = await _store.CreateTokenAsync(user.Id, now, TokenLifetime, ct);
        return new WebLoginOutcome(WebLoginStatus.Success, token, expires, user);
    }

    /// <summary>校验令牌 → 主体；无效/过期/账号已删返回 null。</summary>
    public async Task<WebPrincipal?> ValidateTokenAsync(string? token, CancellationToken ct = default)
    {
        var row = await _store.ValidateTokenAsync(token, _utcNow(), ct);
        if (row is null) return null;
        var user = await _store.FindByIdAsync(row.UserId, ct);
        if (user is null)
        {
            await _store.RevokeAllTokensAsync(row.UserId, ct);
            return null;
        }
        return new WebPrincipal(user.Id, user.Username, user.Role);
    }

    /// <summary>登出：吊销当前令牌。</summary>
    public Task LogoutAsync(string? token, CancellationToken ct = default)
        => _store.RevokeTokenAsync(token, ct);

    /// <summary>
    /// 改密：校验旧密码 → 重哈希 → 吊销该用户全部令牌（含当前会话，调用方应重发令牌）。
    /// 返回 null 表示成功；否则为失败原因。
    /// </summary>
    public async Task<string?> ChangePasswordAsync(long userId, string oldPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _store.FindByIdAsync(userId, ct);
        if (user is null) return "用户不存在";
        if (!WebAccountsStore.VerifyPassword(user, oldPassword)) return "旧密码不正确";
        await _store.ChangePasswordAsync(userId, newPassword, ct: ct);
        await _store.RevokeAllTokensAsync(userId, ct);
        return null;
    }

    private static void DummyHash(string? password)
    {
        var salt = RandomNumberGenerator.GetBytes(WebAuthPolicy.SaltSize);
        Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, WebAuthPolicy.DefaultIterations, HashAlgorithmName.SHA256, WebAuthPolicy.HashSize);
    }
}
