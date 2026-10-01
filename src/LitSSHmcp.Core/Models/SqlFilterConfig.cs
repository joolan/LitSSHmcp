using System.Text.RegularExpressions;

namespace LitSSHmcp.Core.Models;

public class SqlFilterConfig
{
    public string[] ReadOnlyStatements { get; set; } =
    {
        "select", "show", "explain", "desc", "describe", "with", "table", "help", "use"
    };

    public string[] BlockedPatterns { get; set; } =
    {
        @"\bdrop\s+(database|table|user|index)\b",
        @"\btruncate\b",
        @"\bgrant\b",
        @"\brevoke\b",
        @"\bcreate\s+user\b",
        @"\bset\s+password\b",
        @"\bflush\s+privileges\b",
        @"\bshutdown\b",
        @"\binto\s+(outfile|dumpfile)\b",
        @"\bload\s+file\s*\(",
        @"\bdelete\s+from\b(?![^;]*\bwhere\b)",
        @"\bupdate\b(?![^;]*\bwhere\b)"
    };

    public string[] SensitivePatterns { get; set; } =
    {
        @"\binsert\b",
        @"\bupdate\b",
        @"\bdelete\b",
        @"\balter\b",
        @"\brename\b",
        @"\bcreate\b",
        @"\bcall\b",
        @"\bkill\b",
        @"\bload\s+data\b",
        @"\bset\s+global\b",
        @"\block\s+tables?\b",
        @"\bunlock\s+tables?\b",
        @"\btruncate\b",
        @"\bdrop\b"
    };

    private Regex[]? _compiledBlocked;
    private Regex[]? _compiledSensitive;

    public bool IsReadOnlyStatement(string sql)
    {
        var first = GetFirstKeyword(sql);
        return ReadOnlyStatements.Contains(first);
    }

    public bool IsBlocked(string sql)
    {
        _compiledBlocked ??= BlockedPatterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();
        return _compiledBlocked.Any(r => r.IsMatch(sql));
    }

    public bool IsSensitive(string sql)
    {
        _compiledSensitive ??= SensitivePatterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();
        return _compiledSensitive.Any(r => r.IsMatch(sql));
    }

    public static bool IsMultiStatement(string sql)
    {
        var trimmed = sql.Trim().TrimEnd(';').Trim();
        return trimmed.Contains(';');
    }

    public static string GetFirstKeyword(string sql)
    {
        var cleaned = Regex.Replace(sql, @"^(\s|--[^\n]*\n|/\*.*?\*/)+", "", RegexOptions.Singleline);
        var match = Regex.Match(cleaned, @"^[A-Za-z]+");
        return match.Success ? match.Value.ToLowerInvariant() : string.Empty;
    }
}
