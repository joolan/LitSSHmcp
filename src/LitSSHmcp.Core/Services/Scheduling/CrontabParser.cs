namespace LitSSHmcp.Core.Services.Scheduling;

public sealed class ParsedCronEntry
{
    public int LineIndex { get; init; }
    public string RawLine { get; init; } = string.Empty;
    public bool IsPaused { get; init; }
    public string Expression { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string? User { get; init; }
    public CronSchedule? Schedule { get; init; }
    public string? ParseError { get; init; }
}

public static class CrontabParser
{
    public const string PauseMarker = "#LITSSH_PAUSED#";

    public static IReadOnlyList<ParsedCronEntry> Parse(
        string? text,
        bool hasUserField,
        string defaultUser)
    {
        var entries = new List<ParsedCronEntry>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return entries;
        }

        var lines = Normalize(text).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var isPaused = false;
            if (trimmed.StartsWith(PauseMarker, StringComparison.Ordinal))
            {
                isPaused = true;
                trimmed = trimmed[PauseMarker.Length..].TrimStart();
            }
            else if (trimmed.StartsWith('#'))
            {
                continue;
            }

            if (LooksLikeEnvironment(trimmed))
            {
                continue;
            }

            var fields = trimmed.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5)
            {
                continue;
            }

            var expression = string.Join(' ', fields[..5]);
            var rest = fields.Length == 6 ? fields[5] : string.Empty;
            if (rest.Length == 0)
            {
                continue;
            }

            string command;
            string? user;
            if (hasUserField)
            {
                var parts = rest.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                user = parts[0];
                command = parts[1].TrimStart();
            }
            else
            {
                user = null;
                command = rest;
            }

            if (command.Length == 0)
            {
                continue;
            }

            var parsed = CronSchedule.TryParse(expression, out var schedule, out var error);
            entries.Add(new ParsedCronEntry
            {
                LineIndex = i,
                RawLine = line,
                IsPaused = isPaused,
                Expression = expression,
                Command = command,
                User = user ?? defaultUser,
                Schedule = parsed ? schedule : null,
                ParseError = parsed ? null : error
            });
        }

        return entries;
    }

    public static string SetPaused(string text, int lineIndex, string expectedRawLine, bool paused)
    {
        var normalized = Normalize(text);
        var hadTrailingNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (hadTrailingNewline && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lineIndex < 0 || lineIndex >= lines.Count)
        {
            throw new InvalidOperationException("计划任务所在行已不存在，请刷新后重试。");
        }

        var line = lines[lineIndex];
        if (!string.Equals(line, expectedRawLine, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("计划任务文件已变化，请刷新后重试。");
        }

        var trimmed = line.TrimStart();
        var indent = line[..(line.Length - trimmed.Length)];
        if (paused)
        {
            if (!trimmed.StartsWith(PauseMarker, StringComparison.Ordinal))
            {
                lines[lineIndex] = indent + PauseMarker + " " + trimmed;
            }
        }
        else
        {
            if (!trimmed.StartsWith(PauseMarker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("该任务当前不处于暂停状态。");
            }

            var content = trimmed[PauseMarker.Length..].TrimStart();
            lines[lineIndex] = indent + content;
        }

        var result = string.Join('\n', lines);
        return hadTrailingNewline ? result + "\n" : result;
    }

    public static bool IsPausedLine(string line)
        => line.TrimStart().StartsWith(PauseMarker, StringComparison.Ordinal);

    public static string Normalize(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool LooksLikeEnvironment(string line)
    {
        var equals = line.IndexOf('=');
        if (equals <= 0)
        {
            return false;
        }

        var name = line[..equals].Trim();
        if (name.Length == 0)
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                return false;
            }
        }

        return !char.IsDigit(name[0]);
    }
}
