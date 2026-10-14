using Windows.Security.Credentials.UI;

namespace LitSSHmcp.App.Services;

/// <summary>
/// Windows Hello 快捷解锁（指纹 / PIN / Windows 密码，由系统弹窗验证「本人在场」）。
/// 未配置 Hello 的机器 <see cref="IsAvailableAsync"/> 返回 false，界面隐藏入口、回退密码解锁。
/// 注意：Hello 验证的是系统登录用户在场，不校验本程序的应用锁密码本身——
/// 它是与密码并列的另一种解锁方式，不是密码之上的附加因素。
/// </summary>
public static class WindowsHelloService
{
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> VerifyAsync(string message)
    {
        try
        {
            return await UserConsentVerifier.RequestVerificationAsync(message) == UserConsentVerificationResult.Verified;
        }
        catch
        {
            return false;
        }
    }
}
