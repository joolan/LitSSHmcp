using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 列表分组标题的折叠/展开状态按页面与分组名持久化。
/// 在 ListView 上设置 StorageKey，在分组模板的 Expander 上绑定 GroupName。
/// </summary>
public static class GroupExpanderBehavior
{
    private sealed class State
    {
        public bool LoadedHooked;
        public bool EventsHooked;
        public bool SuppressSave;
    }

    private static readonly ConditionalWeakTable<Expander, State> Attached = new();

    public static readonly DependencyProperty StorageKeyProperty = DependencyProperty.RegisterAttached(
        "StorageKey", typeof(string), typeof(GroupExpanderBehavior), new PropertyMetadata(null));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(GroupExpanderBehavior),
        new PropertyMetadata(false, OnTrackChanged));

    public static readonly DependencyProperty GroupNameProperty = DependencyProperty.RegisterAttached(
        "GroupName", typeof(string), typeof(GroupExpanderBehavior),
        new PropertyMetadata(null, OnGroupNameChanged));

    public static void SetStorageKey(DependencyObject element, string? value)
        => element.SetValue(StorageKeyProperty, value);

    public static string? GetStorageKey(DependencyObject element)
        => (string?)element.GetValue(StorageKeyProperty);

    public static void SetTrack(DependencyObject element, bool value)
        => element.SetValue(TrackProperty, value);

    public static bool GetTrack(DependencyObject element)
        => (bool)element.GetValue(TrackProperty);

    public static void SetGroupName(DependencyObject element, string? value)
        => element.SetValue(GroupNameProperty, value);

    public static string? GetGroupName(DependencyObject element)
        => (string?)element.GetValue(GroupNameProperty);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Expander expander && e.NewValue is true)
            Attach(expander);
    }

    private static void OnGroupNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Expander expander)
            Attach(expander);
    }

    private static void Attach(Expander expander)
    {
        var state = Attached.GetValue(expander, _ => new State());
        if (!state.LoadedHooked)
        {
            state.LoadedHooked = true;
            expander.Loaded += OnExpanderLoaded;
        }

        // 分组容器可能被复用：GroupName 变化时重新套用该分组自己的折叠状态。
        if (expander.IsLoaded)
            ApplySavedState(expander, state);
    }

    private static void OnExpanderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander || !Attached.TryGetValue(expander, out var state))
            return;

        expander.Loaded -= OnExpanderLoaded;
        state.LoadedHooked = false;
        ApplySavedState(expander, state);

        if (state.EventsHooked)
            return;
        state.EventsHooked = true;
        expander.Expanded += OnExpanded;
        expander.Collapsed += OnCollapsed;
    }

    private static void ApplySavedState(Expander expander, State state)
    {
        var key = BuildKey(expander);
        if (key is null)
            return;

        var saved = UiPrefs.GetString(key, "true");
        var expanded = !string.Equals(saved, "false", StringComparison.OrdinalIgnoreCase);
        if (expander.IsExpanded == expanded)
            return;

        state.SuppressSave = true;
        try { expander.IsExpanded = expanded; }
        finally { state.SuppressSave = false; }
    }

    private static void OnExpanded(object sender, RoutedEventArgs e) => Save(sender);

    private static void OnCollapsed(object sender, RoutedEventArgs e) => Save(sender);

    private static void Save(object sender)
    {
        if (sender is not Expander expander ||
            !Attached.TryGetValue(expander, out var state) ||
            state.SuppressSave)
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
