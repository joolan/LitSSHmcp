namespace LitSSHmcp.Core.Services.Sql;

/// <summary>一段 SQL 语句在脚本中的位置与长度（已去除语句前后的空白/注释）。</summary>
public readonly record struct SqlStatement(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// 把 SQL 脚本切分为独立语句（用于「执行当前语句 / 执行选区 / 执行全部」）。
/// 正确处理：单引号字符串（含 '' 转义）、双引号/反引号标识符、行注释（-- 与 #）、块注释（/* */，支持嵌套）、
/// PostgreSQL 美元引用（$tag$ ... $tag$）。分隔符为顶层分号。
/// </summary>
public static class SqlScriptSplitter
{
    public static IReadOnlyList<SqlStatement> Split(string? sql)
    {
        var list = new List<SqlStatement>();
        if (string.IsNullOrEmpty(sql))
            return list;

        var n = sql.Length;
        var segStart = 0;
        var i = 0;
        while (i < n)
        {
            var c = sql[i];
            if (c == '\'' || c == '"' || c == '`')
            {
                i = SkipQuoted(sql, i, c);
                continue;
            }
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                i = SkipLineComment(sql, i);
                continue;
            }
            if (c == '#')
            {
                i = SkipLineComment(sql, i);
                continue;
            }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                i = SkipBlockComment(sql, i);
                continue;
            }
            if (c == '$' && TrySkipDollarQuote(sql, i, out var after))
            {
                i = after;
                continue;
            }
            if (c == ';')
            {
                AddSegment(sql, segStart, i, list);
                segStart = i + 1;
                i++;
                continue;
            }
            i++;
        }
        AddSegment(sql, segStart, n, list);
        return list;
    }

    /// <summary>返回包含/最接近光标的语句（无有效语句时返回 null）。</summary>
    public static SqlStatement? StatementAt(string? sql, int caret)
    {
        var parts = Split(sql);
        if (parts.Count == 0)
            return null;
        if (caret < 0)
            return parts[0];

        foreach (var p in parts)
        {
            if (caret >= p.Start && caret <= p.End)
                return p;
        }

        // 光标落在语句之间的空白/注释处：取前一条，否则第一条
        SqlStatement? prev = null;
        foreach (var p in parts)
        {
            if (p.End <= caret)
                prev = p;
            else
                return prev ?? p;
        }
        return prev ?? parts[0];
    }

    private static void AddSegment(string sql, int start, int end, List<SqlStatement> list)
    {
        var s = start;
        var e = end;

        // 去除首部空白与注释（使“仅注释”的片段被丢弃）
        while (s < e)
        {
            if (char.IsWhiteSpace(sql[s]))
            {
                s++;
                continue;
            }
            if (sql[s] == '-' && s + 1 < e && sql[s + 1] == '-')
            {
                s = SkipLineComment(sql, s);
                continue;
            }
            if (sql[s] == '#')
            {
                s = SkipLineComment(sql, s);
                continue;
            }
            if (sql[s] == '/' && s + 1 < e && sql[s + 1] == '*')
            {
                s = SkipBlockComment(sql, s);
                continue;
            }
            break;
        }

        // 去除尾部空白
        while (e > s && char.IsWhiteSpace(sql[e - 1]))
            e--;

        if (e > s)
            list.Add(new SqlStatement(s, e - s));
    }

    private static int SkipQuoted(string sql, int i, char quote)
    {
        var n = sql.Length;
        var j = i + 1;
        while (j < n)
        {
            if (sql[j] == quote)
            {
                if (j + 1 < n && sql[j + 1] == quote)   // 双写转义
                {
                    j += 2;
                    continue;
                }
                return j + 1;
            }
            j++;
        }
        return n;
    }

    private static int SkipLineComment(string sql, int i)
    {
        var n = sql.Length;
        var j = i;
        while (j < n && sql[j] != '\n')
            j++;
        return j;   // 停在该换行处，外层按空白跳过
    }

    private static int SkipBlockComment(string sql, int i)
    {
        var n = sql.Length;
        var depth = 1;
        var j = i + 2;
        while (j < n)
        {
            if (sql[j] == '/' && j + 1 < n && sql[j + 1] == '*')
            {
                depth++;
                j += 2;
            }
            else if (sql[j] == '*' && j + 1 < n && sql[j + 1] == '/')
            {
                depth--;
                j += 2;
                if (depth == 0)
                    return j;
            }
            else
            {
                j++;
            }
        }
        return n;
    }

    private static bool TrySkipDollarQuote(string sql, int i, out int after)
    {
        after = i;
        var n = sql.Length;
        var k = i + 1;
        while (k < n && (char.IsLetterOrDigit(sql[k]) || sql[k] == '_'))
            k++;
        if (k >= n || sql[k] != '$')
            return false;   // 不是美元引用（例如 $1 参数）

        var tag = sql.Substring(i, k - i + 1);
        var found = sql.IndexOf(tag, k + 1, StringComparison.Ordinal);
        after = found < 0 ? n : found + tag.Length;
        return true;
    }
}
