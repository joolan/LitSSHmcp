using System.Collections.ObjectModel;
using System.ComponentModel;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

/// <summary>AI 助手侧栏的状态与构建：分组折叠偏好、分组标题/会话行扁平项、选中同步。</summary>
internal sealed class AgentSessionSidebar
{
    private readonly Func<IReadOnlyList<AgentSessionRow>> _sessions;
    private readonly Func<bool> _isFiltering;
    private readonly Func<long, string> _stateTextForSession;
    private readonly Action<SidebarItemVM?> _setSelection;
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);

    public AgentSessionSidebar(
        Func<IReadOnlyList<AgentSessionRow>> sessions,
        Func<bool> isFiltering,
        Func<long, string> stateTextForSession,
        Action<SidebarItemVM?> setSelection)
    {
        _sessions = sessions;
        _isFiltering = isFiltering;
        _stateTextForSession = stateTextForSession;
        _setSelection = setSelection;
    }

    /// <summary>已定义的分组名（持久化；空分组也会在侧栏显示）。</summary>
    public ObservableCollection<string> Groups { get; } = new();

    /// <summary>侧栏扁平项：分组标题 + 会话（供带分组的列表渲染）。</summary>
    public ObservableCollection<SidebarItemVM> SidebarItems { get; } = new();

    /// <summary>从本机偏好读取已折叠的分组。</summary>
    public void LoadCollapsed()
    {
        _collapsedGroups.Clear();
        try
        {
            var raw = UiPrefs.GetString("agent.collapsedGroups", string.Empty);
            foreach (var g in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                _collapsedGroups.Add(g.Trim());
        }
        catch
        {
            // 读取失败忽略
        }
    }

    /// <summary>折叠/展开某分组。</summary>
    public void Toggle(string key, AgentSessionRow? selected)
    {
        key ??= string.Empty;
        if (!_collapsedGroups.Remove(key))
            _collapsedGroups.Add(key);
        SaveCollapsed();
        Rebuild(selected);
    }

    /// <summary>重建侧栏扁平项：未分组（有内容时）置顶 + 各命名分组（空也显示）下挂会话。</summary>
    public void Rebuild(AgentSessionRow? selected)
    {
        SidebarItems.Clear();
        var scope = _sessions().ToList();
        var filtering = _isFiltering();

        void AddBucket(string key, string display, bool alwaysShow)
        {
            // scope 已按持久化排序（SortOrder）排列，保持原顺序
            var items = scope
                .Where(s => string.Equals(s.Group ?? string.Empty, key, StringComparison.Ordinal))
                .ToList();
            if (items.Count == 0 && (!alwaysShow || filtering))
                return;
            var collapsed = _collapsedGroups.Contains(key);
            SidebarItems.Add(new SidebarItemVM
            {
                IsHeader = true,
                GroupName = key,
                HeaderText = display,
                Count = items.Count,
                IsCollapsed = collapsed
            });
            if (collapsed)
                return;
            foreach (var s in items)
                SidebarItems.Add(new SidebarItemVM
                {
                    Session = s,
                    StateTextProvider = () => _stateTextForSession(s.Id)
                });
        }

        AddBucket(string.Empty, "未分组", alwaysShow: false);
        foreach (var g in Groups.Where(x => !string.IsNullOrWhiteSpace(x)))
            AddBucket(g, g, alwaysShow: true);

        SyncSelection(selected);
    }

    /// <summary>把选中项同步到当前侧栏中的对应会话行。</summary>
    public void SyncSelection(AgentSessionRow? selected) =>
        _setSelection(selected is null
            ? null
            : SidebarItems.FirstOrDefault(i => i.Session is { } s && s.Id == selected.Id));

    /// <summary>状态机切换后刷新所有会话行徽章。</summary>
    public void RefreshStates()
    {
        foreach (var item in SidebarItems)
            item.RefreshState();
    }

    private void SaveCollapsed()
    {
        try { UiPrefs.SetString("agent.collapsedGroups", string.Join('\n', _collapsedGroups)); }
        catch { /* ignore */ }
    }
}

/// <summary>AI 助手侧栏项：分组标题（<see cref="IsHeader"/>）或一个会话行。</summary>
public sealed class SidebarItemVM : INotifyPropertyChanged
{
    public bool IsHeader { get; init; }
    public string HeaderText { get; init; } = string.Empty;
    public int Count { get; init; }

    /// <summary>分组键（空串=未分组）；标题项用于右键重命名/删除分组。</summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>分组标题是否处于折叠状态。</summary>
    public bool IsCollapsed { get; init; }

    /// <summary>分组标题的展开/折叠指示符（会话项为空）。</summary>
    public string Glyph => IsHeader ? (IsCollapsed ? "▸" : "▾") : string.Empty;

    public AgentSessionRow? Session { get; init; }

    /// <summary>会话状态徽章取值（由 AgentViewModel 按会话 ID 提供；未设置=无徽章）。</summary>
    public Func<string>? StateTextProvider { get; init; }

    /// <summary>会话行的状态徽章文案（空=隐藏；由 RefreshState 触发重新取值）。</summary>
    public string StateText => StateTextProvider?.Invoke() ?? string.Empty;

    /// <summary>状态机切换后重新取值（只对会话行有效）。</summary>
    public void RefreshState()
    {
        if (StateTextProvider is null)
            return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
    }

    private bool _isContextTarget;

    /// <summary>右键菜单打开期间，保持该行的悬浮高亮（避免菜单弹出后悬浮态消失）。</summary>
    public bool IsContextTarget
    {
        get => _isContextTarget;
        set
        {
            if (_isContextTarget == value)
                return;
            _isContextTarget = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsContextTarget)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
