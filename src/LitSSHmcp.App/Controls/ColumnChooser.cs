using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Controls;

/// <summary>管理列表的列显隐/列宽持久化：按当前 GridView 列动态生成菜单，并写入 ui-prefs。</summary>
public static class ColumnChooser
{
    private static readonly ConditionalWeakTable<ListView, object> Attached = new();

    /// <summary>在视图构造时调用：恢复上次列宽/显隐，并在拖拽列宽后自动保存。</summary>
    public static void Attach(ListView listView, string key)
    {
        if (listView.View is not GridView)
            return;

        if (!Attached.TryGetValue(listView, out _))
        {
            Attached.Add(listView, new object());
            Restore(listView, key);
            listView.AddHandler(Thumb.DragCompletedEvent,
                new DragCompletedEventHandler((_, _) => Save(listView, key)));
        }
    }

    public static void Show(FrameworkElement anchor, ListView listView, string key)
    {
        if (listView.View is not GridView gridView)
            return;

        Attach(listView, key);

        var menu = new ContextMenu { PlacementTarget = anchor };
        var savedWidths = new Dictionary<GridViewColumn, double>();
        foreach (var column in gridView.Columns)
        {
            var header = NormalizeHeader(column.Header);
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
                Save(listView, key);
            };
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
            return;

        menu.Closed += (_, _) => Save(listView, key);
        menu.IsOpen = true;
    }

    private static void Restore(ListView listView, string key)
    {
        if (listView.View is not GridView gridView)
            return;

        var saved = Load(key);
        if (saved.Count == 0)
            return;

        foreach (var column in gridView.Columns)
        {
            if (!saved.TryGetValue(NormalizeHeader(column.Header), out var width))
                continue;
            column.Width = width <= 0 ? 0 : width < 0 ? double.NaN : width;
        }
    }

    private static void Save(ListView listView, string key)
    {
        if (listView.View is not GridView gridView)
            return;

        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var column in gridView.Columns)
        {
            var header = NormalizeHeader(column.Header);
            if (string.IsNullOrWhiteSpace(header))
                continue;
            map[header] = column.Width <= 0 ? 0 : double.IsNaN(column.Width) ? -1 : column.Width;
        }

        try { UiPrefs.SetString(key, JsonSerializer.Serialize(map)); }
        catch { /* 偏好保存失败不影响列表 */ }
    }

    private static Dictionary<string, double> Load(string key)
    {
        try
        {
            var text = UiPrefs.GetString(key, string.Empty);
            return string.IsNullOrWhiteSpace(text)
                ? new Dictionary<string, double>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, double>>(text) ?? new(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
    }

    private static string NormalizeHeader(object? header)
        => (header?.ToString() ?? string.Empty).Trim().TrimEnd('▲', '▼').Trim();
}
