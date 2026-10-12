using System.Text;

namespace LitSSHmcp.Core.Services.Sql;

/// <summary>
/// 轻量 SQL 美化：统一大小写与空白，把 SELECT/FROM/WHERE… 等主要子句换行。
/// 仅做“可读性”格式化，不改变语义；字符串/注释内容原样保留。
/// </summary>
public static class SqlScriptFormatter
{
    private static readonly string[] MajorClauses =
    {
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET",
        "UNION ALL", "UNION", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM",
        "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "JOIN", "ON",
        "AND", "OR"
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "UNION", "ALL", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "DISTINCT",
        "AS", "AND", "OR", "NOT", "NULL", "IS", "IN", "LIKE", "BETWEEN", "EXISTS",
        "CASE", "WHEN", "THEN", "ELSE", "END", "JOIN", "INNER", "LEFT", "RIGHT", "FULL",
        "OUTER", "CROSS", "ON", "USING", "ASC", "DESC", "WITH", "EXPLAIN", "CREATE", "ALTER",
        "DROP", "TABLE", "VIEW", "INDEX", "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "DEFAULT",
        "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "CAST", "TRUE", "FALSE"
    };

    public static string Format(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql ?? string.Empty;

        var tokens = Tokenize(sql);
        var sb = new StringBuilder();

        for (var i = 0; i < tokens.Count; i++)
        {
            var raw = tokens[i];
            var isWord = raw.Length > 0 && (char.IsLetter(raw[0]) || raw[0] is '_' or '@' or '$');
            var text = isWord && Keywords.Contains(raw) ? raw.ToUpperInvariant() : raw;

            if (raw == ";")
            {
                sb.Append(';');
                sb.Append('\n');
                continue;
            }

            if (isWord && MatchesMajorClause(tokens, i))
            {
                if (sb.Length > 0 && sb[^1] != '\n')
                    sb.Append('\n');
                sb.Append(text);
                continue;
            }

            if (raw == ",")
            {
                sb.Append(',');
                sb.Append(' ');
                continue;
            }

            if (raw == ")")
            {
                TrimTrailingSpace(sb);
                sb.Append(')');
                continue;
            }

            if (raw == "(")
            {
                var prev = i > 0 ? tokens[i - 1].ToUpperInvariant() : string.Empty;
                var func = prev is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" or "COALESCE" or "CAST";
                if (!func && sb.Length > 0 && sb[^1] != '\n' && sb[^1] != ' ' && sb[^1] != '(')
                    sb.Append(' ');
                sb.Append('(');
                continue;
            }

            if (raw == ".")
            {
                TrimTrailingSpace(sb);
                sb.Append('.');
                continue;
            }

            if (sb.Length > 0 && sb[^1] != '\n' && sb[^1] != ' ' && sb[^1] != '(')
                sb.Append(' ');
            sb.Append(text);
        }

        return sb.ToString().Trim();
    }

    private static bool MatchesMajorClause(List<string> tokens, int i)
    {
        var one = tokens[i].ToUpperInvariant();
        if (one == "JOIN" || one == "ON" || one == "AND" || one == "OR" || one == "SET" || one == "VALUES")
            return true;

        if (one == "GROUP" && i + 1 < tokens.Count && tokens[i + 1].Equals("BY", StringComparison.OrdinalIgnoreCase)
            || one == "ORDER" && i + 1 < tokens.Count && tokens[i + 1].Equals("BY", StringComparison.OrdinalIgnoreCase)
            || one == "UNION" && i + 1 < tokens.Count && tokens[i + 1].Equals("ALL", StringComparison.OrdinalIgnoreCase)
            || one == "INSERT" && i + 1 < tokens.Count && tokens[i + 1].Equals("INTO", StringComparison.OrdinalIgnoreCase)
            || one == "DELETE" && i + 1 < tokens.Count && tokens[i + 1].Equals("FROM", StringComparison.OrdinalIgnoreCase))
            return true;

        if (one is "SELECT" or "FROM" or "WHERE" or "HAVING" or "LIMIT" or "OFFSET" or "UPDATE")
            return true;

        if ((one is "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS") && i + 1 < tokens.Count &&
            tokens[i + 1].Equals("JOIN", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool NeedsSpaceBeforeParen(StringBuilder sb)
        => sb.Length > 0 && (char.IsLetterOrDigit(sb[^1]) || sb[^1] == '_');

    private static void TrimTrailingSpace(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
    }

    private static List<string> Tokenize(string sql)
    {
        var tokens = new List<string>();
        var i = 0;
        var n = sql.Length;
        while (i < n)
        {
            var c = sql[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '\'' || c == '"' || c == '`')
            {
                var j = i + 1;
                while (j < n)
                {
                    if (sql[j] == c)
                    {
                        if (j + 1 < n && sql[j + 1] == c) { j += 2; continue; }
                        j++;
                        break;
                    }
                    j++;
                }
                tokens.Add(sql[i..j]);
                i = j;
                continue;
            }
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                var j = i;
                while (j < n && sql[j] != '\n') j++;
                tokens.Add(sql[i..j].TrimEnd());
                i = j;
                continue;
            }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                var j = i + 2;
                while (j + 1 < n && !(sql[j] == '*' && sql[j + 1] == '/')) j++;
                j = Math.Min(n, j + 2);
                tokens.Add(sql[i..j]);
                i = j;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '$')
            {
                var j = i;
                while (j < n && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_' || sql[j] == '@' || sql[j] == '$' || sql[j] == '.'))
                    j++;
                tokens.Add(sql[i..j]);
                i = j;
                continue;
            }

            tokens.Add(c.ToString());
            i++;
        }
        return tokens;
    }
}
