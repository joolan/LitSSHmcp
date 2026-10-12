using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LitSSHmcp.App.ViewModels;

/// <summary>对象树节点类型。</summary>
public enum DbTreeNodeKind
{
    Folder,
    Table,
    View,
    Procedure,
    Column,
    Index
}

/// <summary>数据库工作台左栏对象树的一个节点（库 → 表/视图/过程 → 列/索引）。</summary>
public sealed class DbTreeNode : INotifyPropertyChanged
{
    public string Name { get; set; } = string.Empty;
    public DbTreeNodeKind Kind { get; init; } = DbTreeNodeKind.Folder;

    /// <summary>稳定标识（用于重建树时保留展开状态，不随名称/计数变化）。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>是否可能含子节点（显示展开箭头）；实际子节点在展开时懒加载。</summary>
    public bool HasChildren { get; init; }

    /// <summary>关联对象（表/视图节点指向 <see cref="LitSSHmcp.Core.Services.Datasource.DbTableInfo"/>）。</summary>
    public object? Tag { get; init; }

    public ObservableCollection<DbTreeNode> Children { get; } = new();

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
