using System.Data;
using LitSSHmcp.Core.Services.Datasource;

namespace LitSSHmcp.App.ViewModels;

/// <summary>把驱动返回的结果集转成 WPF 可绑定的 <see cref="DataView"/>（列名去重、缺失值置 DBNull）。</summary>
internal static class WorkbenchResultView
{
    public static DataView Build(DatasourceQueryResult result)
    {
        var table = new DataTable();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<string>();
        foreach (var col in result.Columns)
        {
            var name = col;
            var i = 1;
            while (!names.Add(name))
                name = $"{col}_{i++}";
            columns.Add(name);
            table.Columns.Add(name, typeof(object));
        }

        foreach (var row in result.Rows)
        {
            var dr = table.NewRow();
            for (var i = 0; i < result.Columns.Length && i < columns.Count; i++)
                dr[columns[i]] = row.TryGetValue(result.Columns[i], out var v) ? v ?? DBNull.Value : DBNull.Value;
            table.Rows.Add(dr);
        }
        return table.DefaultView;
    }
}
