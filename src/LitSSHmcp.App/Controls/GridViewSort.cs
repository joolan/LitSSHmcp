using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 为 `ListView + GridView` 提供**点击列头排序**（附加属性用法：`local:GridViewSort.Enabled="True"`）。
/// 依据列的 DisplayMemberBinding 路径排序，表头显示 ▲/▼。
/// </summary>
public static class GridViewSort
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(GridViewSort), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static readonly RoutedEventHandler Handler = OnHeaderClick;

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView listView)
            return;
        if ((bool)e.NewValue)
            listView.AddHandler(GridViewColumnHeader.ClickEvent, Handler);
        else
            listView.RemoveHandler(GridViewColumnHeader.ClickEvent, Handler);
    }

    private static void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView listView)
            return;
        if (e.OriginalSource is not GridViewColumnHeader header || header.Role == GridViewColumnHeaderRole.Padding)
            return;
        if (header.Column is not { } column)
            return;

        var path = GetSortPath(column);
        if (string.IsNullOrEmpty(path))
            return;

        var view = CollectionViewSource.GetDefaultView(listView.ItemsSource);
        if (view is null)
            return;

        var direction = ListSortDirection.Ascending;
        var existing = view.SortDescriptions.FirstOrDefault();
        if (existing.PropertyName == path && existing.Direction == ListSortDirection.Ascending)
            direction = ListSortDirection.Descending;

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(path, direction));
        }

        if (listView.View is GridView gridView)
            foreach (var col in gridView.Columns)
                col.Header = StripArrow(col.Header) + (ReferenceEquals(col, column) ? (direction == ListSortDirection.Ascending ? " ▲" : " ▼") : string.Empty);
    }

    private static string? GetSortPath(GridViewColumn column) =>
        column.DisplayMemberBinding is Binding { Path: { Path: { Length: > 0 } path } } ? path : null;

    private static string StripArrow(object? header)
    {
        var text = header as string ?? string.Empty;
        return text.TrimEnd(' ', '▲', '▼');
    }
}
