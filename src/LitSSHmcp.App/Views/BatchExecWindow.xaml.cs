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

        // 危险快速命令执行前的确认（"带审批"）
        DataContextChanged += (_, _) =>
        {
            if (DataContext is BatchExecViewModel vm)
                vm.Confirm = action => System.Windows.MessageBox.Show(
                    $"确认在所选服务器上执行「{action.Name}」？\n\n命令：{action.Command}",
                    "批量执行 - 操作确认", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;
        };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

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
