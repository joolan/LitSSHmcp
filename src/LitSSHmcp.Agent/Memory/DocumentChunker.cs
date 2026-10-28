namespace LitSSHmcp.Agent;

/// <summary>
/// 把 Markdown/文本切成可检索片段：按行累积、保留最近标题、带尾部重叠；
/// 围栏代码块内的 <c>#</c> 不当作标题；超长单行按字符硬切，避免整段无法检索。
/// </summary>
public static class DocumentChunker
{
    public const int DefaultMaxChars = 2000;
    public const int DefaultOverlapChars = 200;

    public static IReadOnlyList<string> Chunk(string? text, int maxChars = DefaultMaxChars,
        int overlapChars = DefaultOverlapChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        maxChars = Math.Max(200, maxChars);
        overlapChars = Math.Clamp(overlapChars, 0, maxChars / 2);

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length <= maxChars)
            return new[] { normalized };

        var chunks = new List<string>();
        var current = new List<string>();
        var currentLength = 0;
        var heading = string.Empty;
        var inFence = false;

        void Flush()
        {
            if (current.Count == 0)
                return;

            var chunk = string.Join('\n', current).Trim();
            if (chunk.Length > 0)
                chunks.Add(chunk);

            if (overlapChars > 0)
            {
                current = TakeTailLines(current, overlapChars);
                currentLength = current.Sum(line => line.Length + 1);
            }
            else
            {
                current.Clear();
                currentLength = 0;
            }
        }

        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.TrimStart();
            var fenceLine = trimmed.StartsWith("```", StringComparison.Ordinal);
            var isHeading = !inFence && IsHeading(line);

            if (isHeading)
            {
                heading = line.Trim();
                // 新章节开始且当前块已过半时先落块，让章节尽量保持完整。
                if (current.Count > 0 && currentLength >= maxChars / 2)
                    Flush();
            }

            if (line.Length > maxChars)
            {
                if (current.Count > 0)
                    Flush();
                current.Clear();
                currentLength = 0;
                foreach (var piece in SplitLongLine(line, maxChars))
                {
                    if (current.Count > 0)
                    {
                        var part = string.Join('\n', current).Trim();
                        if (part.Length > 0)
                            chunks.Add(part);
                        current.Clear();
                    }
                    current.Add(piece);
                    currentLength = piece.Length + 1;
                }
                continue;
            }

            var needsHeading = current.Count == 0 && heading.Length > 0 &&
                               !string.Equals(line.Trim(), heading, StringComparison.Ordinal);
            var addition = (needsHeading ? heading.Length + 1 : 0) + line.Length + 1;
            if (current.Count > 0 && currentLength + addition > maxChars)
            {
                Flush();
                needsHeading = current.Count == 0 && heading.Length > 0 &&
                               !string.Equals(line.Trim(), heading, StringComparison.Ordinal);
            }

            if (needsHeading)
            {
                current.Add(heading);
                currentLength += heading.Length + 1;
            }

            current.Add(line);
            currentLength += line.Length + 1;

            if (fenceLine)
                inFence = !inFence;
        }

        Flush();
        return chunks;
    }

    private static bool IsHeading(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '#')
            return false;

        var i = 0;
        while (i < trimmed.Length && trimmed[i] == '#')
            i++;
        return i > 0 && i < trimmed.Length && trimmed[i] == ' ';
    }

    /// <summary>从块尾取若干完整行，累计不超过 overlapChars（至少 1 行），用于下一块开头重叠。</summary>
    private static List<string> TakeTailLines(List<string> lines, int overlapChars)
    {
        var result = new List<string>();
        var length = 0;
        for (var i = lines.Count - 1; i >= 0 && length < overlapChars; i--)
        {
            result.Insert(0, lines[i]);
            length += lines[i].Length + 1;
        }
        return result;
    }

    private static IEnumerable<string> SplitLongLine(string line, int size)
    {
        for (var i = 0; i < line.Length; i += size)
            yield return line.Substring(i, Math.Min(size, line.Length - i));
    }
}
