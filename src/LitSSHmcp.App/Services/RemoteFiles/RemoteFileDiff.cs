namespace LitSSHmcp.App.Services.RemoteFiles;

public enum DiffLineKind
{
    Equal,
    Added,
    Removed,
    Blank
}

public sealed record DiffLine(string Text, DiffLineKind Kind);

public sealed class RemoteFileDiffResult
{
    public IReadOnlyList<DiffLine> Left { get; init; } = Array.Empty<DiffLine>();
    public IReadOnlyList<DiffLine> Right { get; init; } = Array.Empty<DiffLine>();
    public int Added { get; init; }
    public int Removed { get; init; }
}

/// <summary>轻量行级 diff。大文件使用按行对齐的回退，避免 LCS 占用过多内存。</summary>
public static class RemoteFileDiff
{
    private const int MaxLcsLines = 3000;

    public static RemoteFileDiffResult Build(string leftText, string rightText)
    {
        var left = SplitLines(leftText);
        var right = SplitLines(rightText);
        if ((long)left.Length * right.Length > MaxLcsLines * MaxLcsLines ||
            left.Length > 20_000 || right.Length > 20_000)
            return BuildByIndex(left, right);

        var n = left.Length;
        var m = right.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var leftLines = new List<DiffLine>(n + m);
        var rightLines = new List<DiffLine>(n + m);
        var added = 0;
        var removed = 0;
        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal))
            {
                leftLines.Add(new DiffLine(left[x], DiffLineKind.Equal));
                rightLines.Add(new DiffLine(right[y], DiffLineKind.Equal));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                leftLines.Add(new DiffLine(left[x], DiffLineKind.Removed));
                rightLines.Add(new DiffLine(string.Empty, DiffLineKind.Blank));
                removed++;
                x++;
            }
            else
            {
                leftLines.Add(new DiffLine(string.Empty, DiffLineKind.Blank));
                rightLines.Add(new DiffLine(right[y], DiffLineKind.Added));
                added++;
                y++;
            }
        }

        while (x < n)
        {
            leftLines.Add(new DiffLine(left[x++], DiffLineKind.Removed));
            rightLines.Add(new DiffLine(string.Empty, DiffLineKind.Blank));
            removed++;
        }
        while (y < m)
        {
            leftLines.Add(new DiffLine(string.Empty, DiffLineKind.Blank));
            rightLines.Add(new DiffLine(right[y++], DiffLineKind.Added));
            added++;
        }

        return new RemoteFileDiffResult
        {
            Left = leftLines,
            Right = rightLines,
            Added = added,
            Removed = removed
        };
    }

    private static RemoteFileDiffResult BuildByIndex(string[] left, string[] right)
    {
        var leftLines = new List<DiffLine>(Math.Max(left.Length, right.Length));
        var rightLines = new List<DiffLine>(Math.Max(left.Length, right.Length));
        var added = 0;
        var removed = 0;
        var count = Math.Max(left.Length, right.Length);
        for (var i = 0; i < count; i++)
        {
            var l = i < left.Length ? left[i] : null;
            var r = i < right.Length ? right[i] : null;
            var equal = string.Equals(l, r, StringComparison.Ordinal);
            leftLines.Add(new DiffLine(l ?? string.Empty, l is null ? DiffLineKind.Blank : equal ? DiffLineKind.Equal : DiffLineKind.Removed));
            rightLines.Add(new DiffLine(r ?? string.Empty, r is null ? DiffLineKind.Blank : equal ? DiffLineKind.Equal : DiffLineKind.Added));
            if (!equal && l is not null) removed++;
            if (!equal && r is not null) added++;
        }
        return new RemoteFileDiffResult
        {
            Left = leftLines,
            Right = rightLines,
            Added = added,
            Removed = removed
        };
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
}
