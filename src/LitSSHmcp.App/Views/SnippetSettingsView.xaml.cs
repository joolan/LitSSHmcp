using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class SnippetSettingsView : UserControl
{
    private readonly ObservableCollection<CommandSnippet> _items;
    private bool _syncing;

    public SnippetSettingsView()
    {
        InitializeComponent();
        _items = new ObservableCollection<CommandSnippet>(SnippetStore.Load());
        List.ItemsSource = _items;
        List.SelectionChanged += (_, _) => SyncFromSelection();
        NameBox.TextChanged += (_, _) => SyncToSelection();
        CommandBox.TextChanged += (_, _) => SyncToSelection();
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
        SnippetStore.Save(_items);
        StatusText.Text = "已保存";
    }
}
