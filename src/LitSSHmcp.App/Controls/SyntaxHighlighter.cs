using System.Text.RegularExpressions;
using System.Windows.Media;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 轻量语法高亮（无第三方依赖）：按行 token 化，识别注释/字符串/数字/关键字/内置命令。
/// 覆盖常见运维语言：bash/sh、json、yaml、sql、java/kotlin/js、properties/ini；未知语言仅做字符串/数字着色。
/// </summary>
public static class SyntaxHighlighter
{
    public static readonly Brush KeywordBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0xC0));
    public static readonly Brush StringBrush = new SolidColorBrush(Color.FromRgb(0xA3, 0x15, 0x15));
    public static readonly Brush NumberBrush = new SolidColorBrush(Color.FromRgb(0x09, 0x86, 0x58));
    public static readonly Brush CommentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x00));
    public static readonly Brush DefaultBrush = Brushes.Black;

    private static readonly Regex TokenPattern = new(
        @"(?<str>""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')|(?<num>\b\d+(?:\.\d+)?\b)|(?<kw>\b[A-Za-z_][A-Za-z0-9_-]*\b)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> BashKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case", "esac", "function", "in",
        "export", "source", "echo", "printf", "cat", "grep", "sed", "awk", "systemctl", "journalctl", "ss",
        "ps", "df", "free", "tail", "head", "curl", "docker", "kubectl", "sudo", "su", "readlink", "tr", "while"
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "insert", "update", "delete", "join", "left", "right", "inner", "outer",
        "group", "order", "by", "having", "limit", "offset", "and", "or", "not", "null", "is", "as", "on",
        "create", "table", "index", "drop", "alter", "values", "into", "show", "explain", "use", "database",
        "primary", "key", "foreign", "references", "distinct", "union", "all", "count", "sum", "avg", "min", "max"
    };

    private static readonly HashSet<string> JavaKeywords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "class", "interface", "enum", "extends", "implements", "static",
        "final", "void", "int", "long", "double", "float", "boolean", "char", "byte", "short", "new", "return",
        "if", "else", "for", "while", "do", "switch", "case", "break", "continue", "try", "catch", "finally",
        "throw", "throws", "import", "package", "this", "super", "null", "true", "false", "var", "const", "let",
        "function", "async", "await", "=>", "true", "false", "package", "import", "export", "default"
    };

    private static readonly HashSet<string> JsonLiterals = new(StringComparer.Ordinal)
    {
        "true", "false", "null"
    };

    public sealed record Token(string Text, Brush Brush, bool Bold = false);

    /// <summary>把代码高亮为若干 (文本, 颜色) 片段；<paramref name="language"/> 大小写不敏感。</summary>
    public static List<Token> Highlight(string code, string? language)
    {
        var keyword = ThemeBrushes.Pick("AppCodeKeywordBrush", KeywordBrush);
        var stringBrush = ThemeBrushes.Pick("AppCodeStringBrush", StringBrush);
        var number = ThemeBrushes.Pick("AppCodeNumberBrush", NumberBrush);
        var comment = ThemeBrushes.Pick("AppCodeCommentBrush", CommentBrush);
        var fallback = ThemeBrushes.Pick("AppCodeTextBrush", DefaultBrush);

        var lang = Normalize(language);
        var tokens = new List<Token>();

        foreach (var rawLine in code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var commentIndex = CommentStart(rawLine, lang);
            var codePart = commentIndex >= 0 ? rawLine[..commentIndex] : rawLine;
            var commentPart = commentIndex >= 0 ? rawLine[commentIndex..] : null;

            HighlightCodePart(codePart, lang, tokens, keyword, stringBrush, number, fallback);
            if (commentPart is not null)
                tokens.Add(new Token(commentPart, comment));

            tokens.Add(new Token("\n", fallback));
        }

        if (tokens.Count > 0 && tokens[^1].Text == "\n")
            tokens.RemoveAt(tokens.Count - 1);
        return tokens;
    }

    private static void HighlightCodePart(string text, string lang, List<Token> tokens, Brush keyword, Brush stringBrush, Brush number, Brush fallback)
    {
        var pos = 0;
        foreach (Match match in TokenPattern.Matches(text))
        {
            if (match.Index > pos)
                tokens.Add(new Token(text[pos..match.Index], fallback));

            var value = match.Value;
            if (match.Groups["str"].Success)
                tokens.Add(new Token(value, stringBrush));
            else if (match.Groups["num"].Success)
                tokens.Add(new Token(value, number));
            else if (IsKeyword(value, lang))
                tokens.Add(new Token(value, keyword, Bold: true));
            else
                tokens.Add(new Token(value, fallback));

            pos = match.Index + match.Length;
        }

        if (pos < text.Length)
            tokens.Add(new Token(text[pos..], fallback));
    }

    private static bool IsKeyword(string word, string lang) => lang switch
    {
        "bash" or "sh" or "shell" => BashKeywords.Contains(word),
        "sql" or "mysql" => SqlKeywords.Contains(word),
        "java" or "kotlin" or "js" or "javascript" or "ts" or "typescript" or "c" or "csharp" or "cs" => JavaKeywords.Contains(word),
        "json" => JsonLiterals.Contains(word),
        "yaml" or "yml" or "properties" or "ini" or "conf" => BashKeywords.Contains(word),  // 值里常见命令/关键字
        _ => false
    };

    private static int CommentStart(string line, string lang)
    {
        var markers = MarkersFor(lang);
        if (markers.Length == 0)
            return -1;

        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && (inSingle || inDouble)) { i++; continue; }
            if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
            if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }
            if (inSingle || inDouble) continue;

            foreach (var marker in markers)
                if (i + marker.Length <= line.Length && string.CompareOrdinal(line, i, marker, 0, marker.Length) == 0)
                    return i;
        }
        return -1;
    }

    private static string[] MarkersFor(string lang) => lang switch
    {
        "sql" or "mysql" => new[] { "--" },
        "java" or "kotlin" or "js" or "javascript" or "ts" or "typescript" or "c" or "csharp" or "cs" => new[] { "//" },
        "bash" or "sh" or "shell" or "yaml" or "yml" or "properties" or "ini" or "conf" or "dockerfile" => new[] { "#" },
        _ => Array.Empty<string>()
    };

    private static string Normalize(string? language)
    {
        var lang = (language ?? string.Empty).Trim().ToLowerInvariant();
        // 去掉 ```json 后面可能的额外信息
        var space = lang.IndexOfAny(new[] { ' ', ',', '{' });
        return space > 0 ? lang[..space] : lang;
    }
}
