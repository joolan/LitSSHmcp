using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 持久化前的不可逆敏感信息脱敏（P2-5）：密码、token、API key、连接串、Cookie、
/// URL userinfo、Bearer/Basic 凭据、私钥块与常见云厂商密钥统一替换为 <c>******</c>。
/// 仅用于审计/会话日志/自动放行规则等落盘场景，不改变实际执行内容或返回给模型的结果。
/// </summary>
public static class SecretRedactor
{
    private const string Mask = "******";

    private static readonly Regex PrivateKeyBlock = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex CliSecret = new(
        @"(?<prefix>--(?:password|passwd|pwd|token|api[-_]?key|secret|client[-_]?secret|passphrase|access[-_]?key|private[-_]?key)(?:\s*=\s*|\s+))(?<value>""[^""]*""|'[^']*'|\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AssignmentSecret = new(
        @"(?<prefix>\b(?:password|passwd|pwd|token|api[-_]?key|secret|client[-_]?secret|passphrase)\s*=\s*)(?<value>""[^""]*""|'[^']*'|[^;\s&,]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UrlUserInfo = new(
        @"(?<prefix>\b[a-z][a-z0-9+.-]*://[^/\s:@]+):(?<value>[^@\s/]+)@",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AuthorizationHeader = new(
        @"(?<prefix>\bAuthorization\s*:\s*(?:Bearer|Basic)\s+)(?<value>[^\s\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BareBearer = new(
        @"(?<prefix>\bBearer\s+)(?<value>[A-Za-z0-9._~+/-]{12,}=*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CookieHeader = new(
        @"(?<prefix>\b(?:Cookie|Set-Cookie)\s*:\s*)(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EnvSecret = new(
        @"(?<prefix>(?:export\s+|set\s+|\$env:)[A-Za-z_][A-Za-z0-9_]*(?:TOKEN|SECRET|PASSWORD|PASSWD|KEY)[A-Za-z0-9_]*\s*=\s*)(?<value>""[^""]*""|'[^']*'|[^\s;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CommonToken = new(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9]{20,})\b",
        RegexOptions.Compiled);

    private static readonly Regex Jwt = new(
        @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        RegexOptions.Compiled);

    /// <summary>脱敏文本；返回结果是否发生变化。</summary>
    public static (string Text, bool Changed) RedactWithChange(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return (text ?? string.Empty, false);

        var result = PrivateKeyBlock.Replace(text, "[REDACTED PRIVATE KEY]");
        result = ReplaceValue(CliSecret, result);
        result = ReplaceValue(AssignmentSecret, result);
        result = UrlUserInfo.Replace(result, "${prefix}:" + Mask + "@");
        result = ReplaceValue(AuthorizationHeader, result);
        result = ReplaceValue(BareBearer, result);
        result = CookieHeader.Replace(result, "${prefix}" + Mask);
        result = ReplaceValue(EnvSecret, result);
        result = CommonToken.Replace(result, Mask);
        result = Jwt.Replace(result, Mask);

        return (result, !string.Equals(result, text, StringComparison.Ordinal));
    }

    public static string Redact(string? text) => RedactWithChange(text).Text;

    /// <summary>替换调用方已知的密钥（如服务器密码/口令），按长度降序避免短串先命中。</summary>
    public static string RedactKnown(string? text, IEnumerable<string?>? knownSecrets)
    {
        if (string.IsNullOrEmpty(text) || knownSecrets is null)
            return text ?? string.Empty;

        var result = text;
        foreach (var secret in knownSecrets
                     .Where(s => !string.IsNullOrWhiteSpace(s) && s!.Length >= 4)
                     .Select(s => s!)
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(s => s.Length))
        {
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }
        return result;
    }

    public static bool ContainsSensitive(string? text) => RedactWithChange(text).Changed;

    /// <summary>精确命令的稳定哈希（用于 AlwaysAllow，不落明文命令）。</summary>
    public static string CommandHash(string command)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((command ?? string.Empty).Trim())));

    private static string ReplaceValue(Regex regex, string input)
        => regex.Replace(input, m => m.Groups["prefix"].Value + Mask);
}
