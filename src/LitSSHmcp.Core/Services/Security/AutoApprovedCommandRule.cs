namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// AlwaysAllow 规则编码：落盘只保存命令 SHA-256 与脱敏展示文本，不保存原始命令。
/// 新格式 <c>sha256:&lt;hex&gt; | &lt;redacted&gt;</c>；旧纯文本规则仍可匹配，并在配置加载时迁移。
/// </summary>
public static class AutoApprovedCommandRule
{
    public const string Prefix = "sha256:";
    private const string Separator = " | ";

    public static string Create(string command)
    {
        var normalized = (command ?? string.Empty).Trim();
        return Prefix + SecretRedactor.CommandHash(normalized) + Separator + SecretRedactor.Redact(normalized);
    }

    public static bool IsEncoded(string? rule)
        => rule is not null && rule.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool Matches(string? command, string? rule)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(rule))
            return false;

        if (!IsEncoded(rule))
        {
            // 兼容迁移前的纯文本规则；配置加载后会转换为哈希格式。
            return string.Equals(command.Trim(), rule.Trim(), StringComparison.Ordinal);
        }

        var rest = rule.Trim()[Prefix.Length..];
        var end = rest.IndexOf(Separator, StringComparison.Ordinal);
        var hash = (end >= 0 ? rest[..end] : rest).Trim();
        return string.Equals(hash, SecretRedactor.CommandHash(command), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>给 UI 展示的脱敏文本；旧规则也会立即脱敏。</summary>
    public static string Display(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
            return string.Empty;
        if (!IsEncoded(rule))
            return SecretRedactor.Redact(rule);

        var rest = rule.Trim()[Prefix.Length..];
        var end = rest.IndexOf(Separator, StringComparison.Ordinal);
        return end >= 0 ? rest[(end + Separator.Length)..] : Prefix + "…";
    }

    /// <summary>迁移配置中的旧明文规则；返回是否有改动。</summary>
    public static bool Migrate(string[]? rules, out string[] migrated)
    {
        if (rules is not { Length: > 0 })
        {
            migrated = Array.Empty<string>();
            return false;
        }

        if (rules.All(IsEncoded))
        {
            migrated = rules;
            return false;
        }

        migrated = rules.Select(Create).Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }
}
