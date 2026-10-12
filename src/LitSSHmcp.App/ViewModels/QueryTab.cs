using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 数据库工作台的一个查询标签：保存该标签独立的 SQL、结果、分页、写模式与状态。
/// （工作台同一时刻只渲染“活动标签”，切换时在标签间存取状态——编辑器只有一个实例。）
/// </summary>
public sealed class QueryTab : INotifyPropertyChanged
{
    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public string SqlText { get; set; } = string.Empty;
    public DataView? Results { get; set; }
    public int PageSize { get; set; } = 100;
    public int PageIndex { get; set; }
    public long? TotalRows { get; set; }

    /// <summary>是否处于分页预览（表预览）状态；仅此时显示分页控件。</summary>
    public bool Paged { get; set; }

    /// <summary>分页基准 SQL（不含 LIMIT/OFFSET）。</summary>
    public string? PageBaseSql { get; set; }

    public bool IsWriteMode { get; set; }
    public string StatusMessage { get; set; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一次查询的一个结果集（多结果集时的子标签）。</summary>
public sealed class ResultSetRef : INotifyPropertyChanged
{
    public int Index { get; init; }
    public string Label { get; init; } = string.Empty;
    public DataView? View { get; init; }

    /// <summary>标签类型：结果集 / 消息 / 执行计划。</summary>
    public ResultTabKind Kind { get; init; } = ResultTabKind.Result;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>结果区子标签类型。</summary>
public enum ResultTabKind
{
    Result,
    Messages,
    Plan
}
