namespace LitSSHmcp.Agent;

/// <summary>
/// 构造记忆召回查询：正常问题保持原样；短追问（继续/那台机器呢/然后呢）拼接最近用户文本与已解析资产。
/// 不调用模型，避免额外延迟和不可控成本。
/// </summary>
public static class MemoryRecallQueryBuilder
{
    public const int DefaultMaxChars = 512;
    private const int MaxRecentMessages = 2;
    private const int MaxRecentChars = 180;

    private static readonly string[] FollowUpMarkers =
    {
        "继续", "然后", "接着", "还有", "那台", "这台", "这个", "那个", "它", "同上",
        "前面", "刚才", "上面", "再看看", "再看", "呢", "怎么办"
    };

    public static string Build(string? userText, IEnumerable<string>? recentUserTexts,
        IReadOnlyCollection<string>? assetIds, int maxChars = DefaultMaxChars)
    {
        var current = Normalize(userText);
        if (current.Length == 0)
            return string.Empty;

        var budget = Math.Max(64, maxChars);
        var parts = new List<string>();

        var assets = (assetIds ?? Array.Empty<string>())
            .Select(Normalize)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(a => !current.Contains(a, StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToList();
        if (assets.Count > 0)
            parts.Add("资产: " + string.Join(", ", assets));

        if (IsFollowUp(current))
        {
            var recent = (recentUserTexts ?? Enumerable.Empty<string>())
                .Select(Normalize)
                .Where(t => t.Length > 0 && !string.Equals(t, current, StringComparison.Ordinal))
                .TakeLast(MaxRecentMessages)
                .Select(t => Truncate(t, MaxRecentChars))
                .ToList();
            if (recent.Count > 0)
                parts.Add("上文: " + string.Join(" | ", recent));
        }

        if (parts.Count == 0)
            return Truncate(current, budget);

        var prefix = string.Join(" ", parts);
        var available = budget - current.Length - 1;
        if (available <= 0)
            return Truncate(current, budget);
        if (prefix.Length > available)
            prefix = prefix[..available].TrimEnd();
        return string.IsNullOrWhiteSpace(prefix) ? Truncate(current, budget) : $"{prefix} {current}";
    }

    private static bool IsFollowUp(string text) =>
        text.Length <= 4 || FollowUpMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? text) =>
        (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd();
}
