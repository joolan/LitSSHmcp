using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class BatchExecWindow : FluentWindow
{
    public BatchExecWindow()
    {
        InitializeComponent();
        WindowLayout.Attach(this, "batch-exec");
    }

    private void OnSnippets(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BatchExecViewModel vm)
            return;

        var menu = new System.Windows.Controls.ContextMenu();
        var added = 0;
        foreach (var snippet in SnippetStore.LoadGlobal())
        {
            if (string.IsNullOrWhiteSpace(snippet.Command))
                continue;
            var item = new System.Windows.Controls.MenuItem
            {
                Header = string.IsNullOrWhiteSpace(snippet.Name) ? snippet.Command : snippet.Name,
                ToolTip = snippet.Command
            };
            var command = snippet.Command;
            item.Click += (_, _) => vm.Command = command;
            menu.Items.Add(item);
            added++;
        }
        if (added == 0)
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "（无全局片段，可在 设置 → 命令片段 添加）", IsEnabled = false });

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
