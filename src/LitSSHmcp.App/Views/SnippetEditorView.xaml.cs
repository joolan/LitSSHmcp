using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

/// <summary>命令片段编辑器：serverId 为空时编辑全局片段，否则编辑该服务器的片段。</summary>
public partial class SnippetEditorView : UserControl
{
    private readonly ObservableCollection<CommandSnippet> _items = new();
    private string? _serverId;
    private bool _syncing;

    public SnippetEditorView()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        List.SelectionChanged += (_, _) => SyncFromSelection();
        NameBox.TextChanged += (_, _) => SyncToSelection();
        CommandBox.TextChanged += (_, _) => SyncToSelection();
        Reload();
    }

    /// <summary>配置为编辑某服务器的片段（serverId 为空=全局）。</summary>
    public void Configure(string? serverId, string? title = null)
    {
        _serverId = serverId;
        HintText.Text = serverId is null
            ? "全局命令片段：对所有 SSH 服务器可见。"
            : $"服务器「{title}」专属命令片段：仅在该服务器的终端「片段」菜单显示。";
        Reload();
    }

    private void Reload()
    {
        _items.Clear();
        var source = _serverId is null ? SnippetStore.LoadGlobal() : SnippetStore.LoadServer(_serverId);
        foreach (var snippet in source)
            _items.Add(snippet);
        if (_items.Count > 0)
            List.SelectedIndex = 0;
    }

    private void SyncFromSelection()
    {
        if (List.SelectedItem is not CommandSnippet snippet)
            return;
        _syncing = true;
        NameBox.Text = snippet.Name;
        CommandBox.Text = snippet.Command;
        _syncing = false;
    }

    private void SyncToSelection()
    {
        if (_syncing || List.SelectedItem is not CommandSnippet snippet)
            return;
        snippet.Name = NameBox.Text;
        snippet.Command = CommandBox.Text;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var snippet = new CommandSnippet("新片段", string.Empty);
        _items.Add(snippet);
        List.SelectedItem = snippet;
        NameBox.Focus();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is CommandSnippet snippet)
            _items.Remove(snippet);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        SyncToSelection();
        if (_serverId is null)
            SnippetStore.SaveGlobal(_items);
        else
            SnippetStore.SaveServer(_serverId, _items);
        StatusText.Text = "已保存";
    }
}
