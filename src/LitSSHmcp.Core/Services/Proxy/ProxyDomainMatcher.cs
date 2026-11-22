namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>
/// 目标域名白名单/黑名单。规则语义：
/// <list type="bullet">
/// <item><c>example.com</c>：匹配 example.com 及其子域；</item>
/// <item><c>*.example.com</c>：只匹配子域；</item>
/// <item><c>.example.com</c>：匹配 example.com 及其子域；</item>
/// <item><c>*</c>：匹配所有目标。</item>
/// </list>
/// 黑名单优先于白名单；白名单为空表示不限制。
/// </summary>
public static class ProxyDomainMatcher
{
    public static string? Evaluate(string host, IReadOnlyList<string>? allowed,
        IReadOnlyList<string>? blocked)
    {
        var value = NormalizeHost(host);

        if (blocked is not null)
        {
            foreach (var pattern in blocked)
            {
                if (IsMatch(value, pattern))
                    return $"目标域名 {host} 在代理黑名单中。";
            }
        }

        if (allowed is null || allowed.Count == 0)
            return null;

        foreach (var pattern in allowed)
        {
            if (IsMatch(value, pattern))
                return null;
        }
        return $"目标域名 {host} 不在代理白名单中。";
    }

    public static bool IsMatch(string host, string pattern)
    {
        var value = NormalizeHost(host);
        var rule = (pattern ?? string.Empty).Trim().ToLowerInvariant();
        if (rule.Length == 0)
            return false;
        if (rule == "*")
            return true;

        if (rule.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = rule[2..];
            return value.Length > suffix.Length &&
                   value.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
        }

        if (rule.Length > 0 && rule[0] == '.')
            rule = rule[1..];

        return value.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith("." + rule, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidPattern(string pattern)
    {
        var rule = (pattern ?? string.Empty).Trim();
        if (rule.Length == 0)
            return false;
        if (rule == "*")
            return true;
        if (rule.Contains("://", StringComparison.Ordinal) ||
            rule.Contains('/', StringComparison.Ordinal) ||
            rule.Any(char.IsWhiteSpace))
            return false;

        if (rule.StartsWith("*.", StringComparison.Ordinal))
            rule = rule[2..];
        else if (rule.Length > 0 && rule[0] == '.')
            rule = rule[1..];

        return rule.Length > 0 && rule.Split('.').All(label => label.Length > 0);
    }

    private static string NormalizeHost(string host) =>
        (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
}
