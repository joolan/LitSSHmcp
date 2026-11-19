using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.App.Services.DatasourceAi;

public sealed class DatasourceAiContextBuilder
{
    private readonly IDatasourceDriverRegistry _registry;
    private const int MaxTableNames = 200;
    private const int MaxTableListChars = 12_000;
    private const int MaxResultChars = 12_000;

    public DatasourceAiContextBuilder(IDatasourceDriverRegistry registry)
        => _registry = registry;

    public async Task<string> BuildSchemaDigestAsync(
        DatasourceAiScope scope, DbTableInfo? selectedTable, CancellationToken ct = default)
    {
        var driver = _registry.GetRequired(scope.Kind);
        if (driver is not IDatabaseExplorer explorer)
            return $"当前数据源类型 {scope.Kind} 不支持结构浏览。";

        var effective = scope.Effective;
        var tables = await explorer.ListTablesAsync(effective, scope.Database, ct);
        var sb = new StringBuilder();
        sb.AppendLine("【数据库作用域】");
        sb.AppendLine($"数据源：{scope.Datasource.Name}（{scope.Kind}）");
        sb.AppendLine($"当前库：{scope.Database}");
        sb.AppendLine($"表数量：{tables.Count}");

        if (selectedTable is not null)
        {
            var actual = tables.FirstOrDefault(t =>
                             string.Equals(t.Name, selectedTable.Name, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(t.Schema, selectedTable.Schema, StringComparison.OrdinalIgnoreCase))
                         ?? selectedTable;
            var columns = await explorer.ListColumnsAsync(
                effective, scope.Database, actual.Name, actual.Schema, ct);
            sb.AppendLine();
            sb.AppendLine($"【当前选中表】{actual.Display}");
            if (!string.IsNullOrWhiteSpace(actual.Comment))
                sb.AppendLine($"表注释：{actual.Comment}");
            if (actual.Rows is long rows)
                sb.AppendLine($"估算行数：{rows:N0}");
            sb.AppendLine("字段：");
            foreach (var column in columns)
            {
                var flags = new List<string>();
                if (column.PrimaryKey) flags.Add("PK");
                if (!column.Nullable) flags.Add("NOT NULL");
                if (column.Default is not null) flags.Add("DEFAULT " + column.Default);
                var suffix = flags.Count > 0 ? " [" + string.Join(", ", flags) + "]" : string.Empty;
                var comment = string.IsNullOrWhiteSpace(column.Comment) ? string.Empty : " // " + column.Comment;
                sb.AppendLine($"- {column.Name} {column.Type}{suffix}{comment}");
            }
        }

        var otherTables = tables
            .Where(t => selectedTable is null ||
                        !string.Equals(t.Name, selectedTable.Name, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(t.Schema, selectedTable.Schema, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Display)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        sb.AppendLine();
        sb.AppendLine("【其他表】");
        if (otherTables.Count == 0)
        {
            sb.AppendLine("（无）");
        }
        else
        {
            var shown = 0;
            var used = 0;
            foreach (var table in otherTables)
            {
                if (shown >= MaxTableNames) break;
                var cost = table.Length + 2;
                if (used + cost > MaxTableListChars) break;
                sb.AppendLine("- " + table);
                shown++;
                used += cost;
            }
            if (shown < otherTables.Count)
                sb.AppendLine($"… 已截断（共 {otherTables.Count} 张；可用 datasource_schema 指定表名查询）");
        }

        return sb.ToString().TrimEnd();
    }

    public static string BuildResultDigest(
        DatasourceQueryResult result,
        MaskingConfig? masking,
        int maxRows = 20,
        int maxChars = MaxResultChars,
        bool isSubset = false)
    {
        ResultMasker.Apply(result.Columns, result.Rows, masking);
        var sb = new StringBuilder();
        sb.AppendLine($"成功：{(result.Success ? "是" : "否")}");
        if (!result.Success)
            sb.AppendLine("错误：" + (result.Error ?? "未知错误"));
        sb.AppendLine($"列：{(result.Columns.Length == 0 ? "（无）" : string.Join(", ", result.Columns))}");

        // RowCount 是驱动本次实际取回的行数，不是数据库真实总数。
        // Truncated=true 表示还有更多行未取回，真实总数未知。
        sb.AppendLine($"本次查询取回：{result.RowCount:N0} 行");
        sb.AppendLine(result.Truncated
            ? "查询截断：是（还有更多行未取回，真实总数未知；需要总数请执行 COUNT(*) 或聚合查询）"
            : "查询截断：否（本次查询返回的就是全部结果）");
        sb.AppendLine($"耗时：{result.DurationMs:F0} ms");

        var limit = Math.Clamp(maxRows, 0, 200);
        var available = result.Rows.Take(limit).ToList();
        var rowLines = new List<string>(available.Count);
        foreach (var row in available)
        {
            var cells = result.Columns.Select(column =>
            {
                row.TryGetValue(column, out var value);
                return FormatCell(value);
            });
            rowLines.Add("| " + string.Join(" | ", cells) + " |");
        }

        // 先确定字符预算能放下多少行，再写“AI 可见”说明，避免摘要被硬截断后含义不清。
        var shown = 0;
        var used = 0;
        foreach (var line in rowLines)
        {
            if (used + line.Length + 2 > maxChars)
                break;
            used += line.Length + 2;
            shown++;
        }

        sb.AppendLine(isSubset
            ? $"AI 可见：{shown} 行（用户选择的行；未选中的行未发送）"
            : $"AI 可见：前 {shown} 行（最多 {limit} 行；完整结果未全部注入）");
        if (available.Count == 0)
        {
            sb.AppendLine("（无结果行）");
        }
        else
        {
            if (shown > 0)
                sb.AppendLine(isSubset ? "选中行：" : $"前 {shown} 行：");
            for (var i = 0; i < shown; i++)
                sb.AppendLine(rowLines[i]);
            if (shown < available.Count)
                sb.AppendLine($"… 另有 {available.Count - shown} 行未注入（字符上限）");
        }

        var text = sb.ToString().TrimEnd();
        return text.Length <= maxChars ? text : text[..maxChars] + "\n…（已截断）";
    }

    private static string FormatCell(object? value)
    {
        if (value is null || value is DBNull)
            return "NULL";
        var text = value.ToString() ?? string.Empty;
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > 240)
            text = text[..240] + "…";
        return text.Replace("|", "\\|");
    }
}
