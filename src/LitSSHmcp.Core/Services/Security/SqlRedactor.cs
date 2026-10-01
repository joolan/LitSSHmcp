using System.Text.RegularExpressions;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>SQL 审计脱敏：将字符串/数字字面量替换为 ?。</summary>
public static class SqlRedactor
{
    private static readonly Regex StringLiteral =
        new(@"'(?:[^']|'')*'", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex DoubleQuotedLiteral =
        new("\"(?:[^\"]|\"\")*\"", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex NumberLiteral =
        new(@"(?<![\w.])\d+(?:\.\d+)?(?![\w.])", RegexOptions.Compiled);

    public static string Mask(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return sql;

        var masked = StringLiteral.Replace(sql, "'?'");
        masked = DoubleQuotedLiteral.Replace(masked, "\"?\"");
        masked = NumberLiteral.Replace(masked, "?");
        return masked;
    }
}
