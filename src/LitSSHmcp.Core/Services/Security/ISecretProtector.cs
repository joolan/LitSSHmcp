using System.Security.Cryptography;
using System.Text;

namespace LitSSHmcp.Core.Services.Security;

public interface ISecretProtector
{
    bool IsProtected(string? value);
    string? Protect(string? value);
    string? Unprotect(string? value);
}

/// <summary>
/// Windows DPAPI (CurrentUser) 加密保护。密文格式: enc:&lt;base64&gt;。
/// 仅本机当前 Windows 用户可解密，密码不会以明文落盘。
/// </summary>
public class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "enc:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LitSSHmcp.Secret.v1");

    public bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (IsProtected(value)) return value;
        if (!OperatingSystem.IsWindows()) return value;

        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!IsProtected(value)) return value;
        if (!OperatingSystem.IsWindows()) return value;

        try
        {
            var protectedBytes = Convert.FromBase64String(value[Prefix.Length..]);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            // 密文可能来自其它机器/用户，保留原值，连接时会因密码错误失败，不会崩溃
            return value;
        }
    }
}
