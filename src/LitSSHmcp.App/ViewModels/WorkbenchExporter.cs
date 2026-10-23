using System.Data;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LitSSHmcp.Core.Services.Sql;

namespace LitSSHmcp.App.ViewModels;

/// <summary>数据库工作台结果导出（CSV / JSON / XLSX），与 ViewModel 的文件对话框/状态提示解耦。</summary>
public static class WorkbenchExporter
{
    /// <summary>写 CSV（UTF-8 BOM，Excel 兼容）；返回导出行数。</summary>
    public static int WriteCsv(DataView view, string path)
    {
        var table = view.Table;
        if (table is null) return 0;
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        writer.WriteLine(string.Join(",", columns.Select(EscapeCsv)));
        var rows = 0;
        foreach (DataRowView row in view)
        {
            writer.WriteLine(string.Join(",", columns.Select(c => EscapeCsv(row[c]?.ToString() ?? string.Empty))));
            rows++;
        }
        return rows;
    }

    /// <summary>写 JSON 数组（列名→值，NULL 为 null）；返回导出行数。</summary>
    public static int WriteJson(DataView view, string path)
    {
        var table = view.Table;
        if (table is null) return 0;
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        var rows = new List<Dictionary<string, object?>>();
        foreach (DataRowView row in view)
        {
            var obj = new Dictionary<string, object?>();
            foreach (var c in columns)
            {
                var v = row[c];
                obj[c] = v is null || v == DBNull.Value ? null : v;
            }
            rows.Add(obj);
        }

        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return rows.Count;
    }

    /// <summary>写 XLSX（数值保持为数字单元格）；返回导出行数。</summary>
    public static int WriteExcel(DataView view, string path)
    {
        var table = view.Table;
        if (table is null) return 0;
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
        var rows = new List<IReadOnlyList<object?>>();
        foreach (DataRowView row in view)
        {
            var values = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                var v = row[columns[i]];
                values[i] = v is DBNull ? null : v;
            }
            rows.Add(values);
        }

        using var fs = File.Create(path);
        XlsxWriter.Write(fs, columns, rows, string.IsNullOrWhiteSpace(table.TableName) ? "Sheet1" : table.TableName);
        return rows.Count;
    }

    public static string EscapeCsv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
