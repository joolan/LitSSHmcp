namespace LitSSHmcp.Agent;

/// <summary>
/// 工具描述精简：MCP 工具描述多为中文长句（约 1 token/字），发送给模型时截短为"首句/要点 + 指向 mcp_usage_guide"，
/// 可显著降低每次请求的固定 token；完整说明仍可通过 mcp_usage_guide 与技能获取。
/// </summary>
public static class ToolDescriptions
{
    private const int MaxChars = 60;

    public static string Compact(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "见 mcp_usage_guide";

        var text = description.Trim();

        // 取第一个句号/分号/换行前的要点
        var cut = text.Length;
        foreach (var ch in new[] { '。', '；', '\n', ';' })
        {
            var index = text.IndexOf(ch);
            if (index > 0 && index < cut)
                cut = index;
        }

        var first = cut < text.Length ? text[..cut] : text;
        if (first.Length > MaxChars)
            first = first[..MaxChars] + "…";

        var hint = "(详见 mcp_usage_guide)";
        return first.Length + hint.Length < text.Length ? first + hint : first;
    }
}
