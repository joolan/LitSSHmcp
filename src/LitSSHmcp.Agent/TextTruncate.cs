namespace LitSSHmcp.Agent;

/// <summary>
/// UTF-16 代理对安全的截断助手。裸 <c>text[..max]</c> / <c>text[^max..]</c> 可能恰好切断
/// emoji/生僻字的代理对，产生非法 UTF-16 字符串 → 个别 provider 端点直接 400。
/// 仅用于回喂模型上下文的高风险路径；UI 展示类截断不受影响可保持原样。
/// </summary>
internal static class TextTruncate
{
    /// <summary>保留前 maxChars 字符；若切点落在代理对中间则回退一个字符。</summary>
    public static string Head(string text, int maxChars)
    {
        if (maxChars < 0 || text.Length <= maxChars)
            return text;
        var cut = maxChars;
        if (char.IsHighSurrogate(text[cut - 1]) && cut < text.Length && char.IsLowSurrogate(text[cut]))
            cut--;
        return text[..cut];
    }

    /// <summary>保留后 maxChars 字符；若切点落在代理对中间则前进一个字符。</summary>
    public static string Tail(string text, int maxChars)
    {
        if (maxChars < 0 || text.Length <= maxChars)
            return text;
        var start = text.Length - maxChars;
        if (char.IsLowSurrogate(text[start]) && start > 0 && char.IsHighSurrogate(text[start - 1]))
            start++;
        return text[start..];
    }
}
