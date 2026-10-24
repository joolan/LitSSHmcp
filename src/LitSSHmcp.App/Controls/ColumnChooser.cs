using System.Windows;
using System.Windows.Controls;

namespace LitSSHmcp.App.Controls;

/// <summary>管理列表的列显隐菜单：按当前 GridView 列动态生成，隐藏列时记录原宽度。</summary>
public static class ColumnChooser
{
    public static void Show(FrameworkElement anchor, ListView listView)
    {
        if (listView.View is not GridView gridView)
            return;

        var menu = new ContextMenu { PlacementTarget = anchor };
        var savedWidths = new Dictionary<GridViewColumn, double>();
        foreach (var column in gridView.Columns)
        {
            var header = column.Header?.ToString() ?? "列";
            if (string.IsNullOrWhiteSpace(header))
                continue;

            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = column.Width > 0.1 && !double.IsNaN(column.Width),
                Tag = column
            };
            item.Click += (_, _) =>
            {
                if (item.IsChecked)
                {
                    var width = savedWidths.TryGetValue(column, out var saved) && saved > 0 ? saved : double.NaN;
                    column.Width = width;
                }
                else
                {
                    if (!double.IsNaN(column.Width) && column.Width > 0.1)
                        savedWidths[column] = column.Width;
                    column.Width = 0;
                }
            };
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
            return;

        menu.IsOpen = true;
    }
}
