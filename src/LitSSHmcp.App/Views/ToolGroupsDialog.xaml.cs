using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using LitSSHmcp.Core.Models;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>「/工具」的多选对话框：勾选会话可用的工具分组（不勾=不限制）。</summary>
public partial class ToolGroupsDialog : FluentWindow
{
    private bool _ok;

    public ToolGroupsDialog()
    {
        InitializeComponent();
        OptionsList.ItemsSource = Options;
    }

    public ObservableCollection<ToolGroupOption> Options { get; } = new();

    /// <summary>返回 null=取消；否则返回选中的分组键（空列表=不限制）。</summary>
    public static IReadOnlyList<string>? Show(Window? owner, IReadOnlyList<string> current)
    {
        var dlg = new ToolGroupsDialog { Owner = owner };
        var set = new HashSet<string>(current ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in ToolGroups.All)
            dlg.Options.Add(new ToolGroupOption { Key = key, Label = LabelOf(key), Selected = set.Contains(key) });
        dlg.ShowDialog();
        return dlg._ok ? dlg.Options.Where(o => o.Selected).Select(o => o.Key).ToList() : null;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        _ok = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnClear(object sender, RoutedEventArgs e)
    {
        foreach (var o in Options)
            o.Selected = false;
    }

    private static string LabelOf(string key) => key switch
    {
        ToolGroups.Ssh => "SSH 服务器 / 快照",
        ToolGroups.Command => "命令执行 / 提权",
        ToolGroups.FileTransfer => "文件传输",
        ToolGroups.Datasource => "数据源",
        ToolGroups.Mysql => "MySQL",
        ToolGroups.Postgres => "PostgreSQL",
        ToolGroups.Redis => "Redis",
        ToolGroups.Docker => "Docker 容器",
        ToolGroups.Service => "systemd 服务",
        ToolGroups.Log => "日志文件",
        ToolGroups.Java => "JVM",
        ToolGroups.Topology => "拓扑",
        ToolGroups.App => "应用",
        ToolGroups.Guide => "使用指南 / 自检",
        _ => key
    };
}

public sealed class ToolGroupOption : INotifyPropertyChanged
{
    public required string Key { get; init; }
    public required string Label { get; init; }

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
