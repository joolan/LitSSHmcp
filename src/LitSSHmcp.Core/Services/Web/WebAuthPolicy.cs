namespace LitSSHmcp.Core.Services.Web;

/// <summary>
/// Web 账号策略常量与纯函数（登录锁定曲线等），便于单测。
/// 哈希参数与 appLock 先例（AppLockService）保持一致：PBKDF2-SHA256 210k / salt 16B / hash 32B。
/// </summary>
public static class WebAuthPolicy
{
    /// <summary>Web 账号密码最短长度（P2-7：由 6 提到 8；仅影响新建/改密，旧账号仍可登录）。</summary>
    public const int MinPasswordLength = 8;

    public const int DefaultIterations = 210_000;
    public const int SaltSize = 16;
    public const int HashSize = 32;

    /// <summary>连续失败达到该次数开始锁定。</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>锁定起步时长（失败刚达阈值）。</summary>
    public static readonly TimeSpan BaseLockout = TimeSpan.FromMinutes(5);

    /// <summary>锁定时长上限。</summary>
    public static readonly TimeSpan MaxLockout = TimeSpan.FromHours(1);

    /// <summary>用户名规则：3-32 位，字母数字开头，允许 _ - .。</summary>
    public static bool IsValidUsername(string? username)
    {
        if (string.IsNullOrEmpty(username) || username.Length is < 3 or > 32) return false;
        foreach (var c in username)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') continue;
            return false;
        }
        return char.IsAsciiLetterOrDigit(username[0]);
    }

    /// <summary>
    /// 失败锁定曲线：未达阈值 0；刚达阈值 = <see cref="BaseLockout"/>；此后每次失败翻倍，封顶 <see cref="MaxLockout"/>。
    /// </summary>
    public static TimeSpan ComputeLockout(int failedCount)
    {
        if (failedCount < MaxFailedAttempts) return TimeSpan.Zero;
        var extra = failedCount - MaxFailedAttempts;
        var minutes = BaseLockout.TotalMinutes * Math.Pow(2, extra);
        if (minutes >= MaxLockout.TotalMinutes) return MaxLockout;
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>令牌 base64url 化（cookie 友好）。</summary>
    public static string ToBase64Url(string base64)
        => base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
