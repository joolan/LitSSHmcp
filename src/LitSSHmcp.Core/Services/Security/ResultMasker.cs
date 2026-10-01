using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 查询结果的列级脱敏：按列名（正则，忽略大小写）匹配 <see cref="MaskingConfig"/> 规则，
/// 命中即对结果集中该列的所有单元格值脱敏后再返回给 AI。
/// </summary>
public static class ResultMasker
{
    public static void Apply(string[] columns, List<Dictionary<string, object?>> rows, MaskingConfig? config)
    {
        if (config?.Rules is not { Length: > 0 } rules || columns.Length == 0 || rows.Count == 0)
            return;

        // 预先算出命中的 (列索引, 规则)，每列取第一条命中的规则
        var matched = new List<(string Column, MaskRule Rule)>();
        foreach (var column in columns)
        {
            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Column))
                    continue;

                try
                {
                    if (Regex.IsMatch(column, rule.Column, RegexOptions.IgnoreCase))
                    {
                        matched.Add((column, rule));
                        break;
                    }
                }
                catch (ArgumentException)
                {
                    // 忽略非法正则
                }
            }
        }

        if (matched.Count == 0)
            return;

        foreach (var row in rows)
        {
            foreach (var (column, rule) in matched)
            {
                if (!row.TryGetValue(column, out var value) || value is null)
                    continue;

                row[column] = Mask(value.ToString() ?? string.Empty, rule.Mode);
            }
        }
    }

    /// <summary>按模式脱敏单个值。</summary>
    public static string Mask(string value, string? mode)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return (mode ?? "full").Trim().ToLowerInvariant() switch
        {
            "email" => MaskEmail(value),
            "phone" => KeepTail(value, 4),
            "last4" => KeepTail(value, 4),
            _ => "***"
        };
    }

    private static string MaskEmail(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1)
            return "***";

        return value[0] + "***" + value[at..];
    }

    private static string KeepTail(string value, int keep)
    {
        if (value.Length <= keep)
            return new string('*', value.Length);

        return new string('*', value.Length - keep) + value[^keep..];
    }
}
