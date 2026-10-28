namespace LitSSHmcp.Agent;

/// <summary>
/// 工具描述精简：MCP 工具描述多为中文长句（约 1 token/字），发送给模型时做轻量压缩。
/// 经验：多数描述本身承载 <c>action=...</c> 枚举与"易混工具"互斥提示，**不能按 60 字符硬截断**；
/// 这里只对超长描述（&gt; <see cref="MaxFullChars"/>）取首段，其余原样保留，避免丢掉关键路由信息。
/// </summary>
public static class ToolDescriptions
{
    /// <summary>描述本身不超过该长度时原样保留（当前全部工具最长 120 字符，均会原样保留）。</summary>
    private const int MaxFullChars = 200;

    /// <summary>超长描述压缩后的长度上限。</summary>
    private const int MaxChars = 160;

    public static string Compact(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "见 mcp_usage_guide";

        var text = description.Trim();

        // 短描述原样保留：截断会丢掉 action=... 枚举、"写用 xxx" 等关键互斥提示。
        if (text.Length <= MaxFullChars)
            return text;

        // 超长描述只取前 MaxChars 字符，并尽量在分句/分号处收尾，避免把 action 列表截成半句。
        var slice = text[..Math.Min(MaxChars, text.Length)];
        var breakIndex = slice.LastIndexOfAny(new[] { '。', '；', ';', '\n', '，', ',' });
        if (breakIndex >= MaxChars / 2)
            slice = slice[..breakIndex];

        var hint = "(详见 mcp_usage_guide)";
        return slice.TrimEnd() + "…" + hint;
    }
}
