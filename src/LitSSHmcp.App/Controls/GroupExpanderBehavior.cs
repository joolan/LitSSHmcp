using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 让列表分组标题可折叠/展开，并按页面与分组名持久化状态。
/// 在 ListView 上设置 StorageKey，在分组模板的 Expander 上绑定 GroupName。
/// </summary>
public static class GroupExpanderBehavior
{
    private static readonly ConditionalWeakTable<Expander, object> Attached = new();

    public static readonly DependencyProperty StorageKeyProperty = DependencyProperty.RegisterAttached(
        "StorageKey", typeof(string), typeof(GroupExpanderBehavior), new PropertyMetadata(null));

    public static readonly DependencyProperty GroupNameProperty = DependencyProperty.RegisterAttached(
        "GroupName", typeof(string), typeof(GroupExpanderBehavior),
        new PropertyMetadata(null, OnGroupNameChanged));

    public static void SetStorageKey(DependencyObject element, string? value)
        => element.SetValue(StorageKeyProperty, value);

    public static string? GetStorageKey(DependencyObject element)
        => (string?)element.GetValue(StorageKeyProperty);

    public static void SetGroupName(DependencyObject element, string? value)
        => element.SetValue(GroupNameProperty, value);

    public static string? GetGroupName(DependencyObject element)
        => (string?)element.GetValue(GroupNameProperty);

    private static void OnGroupNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Expander expander || Attached.TryGetValue(expander, out _))
            return;

        Attached.Add(expander, new object());
        expander.Loaded += OnExpanderLoaded;
    }

    private static void OnExpanderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander)
            return;

        expander.Loaded -= OnExpanderLoaded;
        var key = BuildKey(expander);
        if (key is not null)
        {
            var saved = UiPrefs.GetString(key, "true");
            expander.IsExpanded = !string.Equals(saved, "false", StringComparison.OrdinalIgnoreCase);
        }

        expander.Expanded += OnExpanded;
        expander.Collapsed += OnCollapsed;
    }

    private static void OnExpanded(object sender, RoutedEventArgs e) => Save(sender);

    private static void OnCollapsed(object sender, RoutedEventArgs e) => Save(sender);

    private static void Save(object sender)
    {
        if (sender is not Expander expander)
            return;

        var key = BuildKey(expander);
        if (key is not null)
            UiPrefs.SetString(key, expander.IsExpanded ? "true" : "false");
    }

    private static string? BuildKey(Expander expander)
    {
        var listView = FindAncestor<ListView>(expander);
        var storageKey = listView is null ? null : GetStorageKey(listView);
        if (string.IsNullOrWhiteSpace(storageKey))
            return null;

        var groupName = GetGroupName(expander);
        var normalized = string.IsNullOrWhiteSpace(groupName) ? "__ungrouped__" : groupName;
        return $"{storageKey}:{normalized}";
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
