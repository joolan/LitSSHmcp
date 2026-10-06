using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Services;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace LitSSHmcp.App;

public partial class MainWindow : FluentWindow
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        WindowLayout.Attach(this, "main");
    }

    private void OnServerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 仅在双击到有效的服务器条目时触发；空白区域双击不打开会话
        if (sender is not ListBox listBox)
            return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item == null)
            return;

        listBox.SelectedItem = item.DataContext;
        ViewModel.ConnectCommand.Execute(null);
    }

    private void OnConnectClick(object sender, RoutedEventArgs e) => ViewModel.ConnectCommand.Execute(null);

    private void OnEditServerClick(object sender, RoutedEventArgs e) => ViewModel.EditServerCommand.Execute(null);

    private void OnDeleteServerClick(object sender, RoutedEventArgs e) => ViewModel.DeleteServerCommand.Execute(null);

    private void OnSnapshotRefreshClick(object sender, RoutedEventArgs e) => ViewModel.SnapshotRefreshCommand.Execute(null);

    private void OnSnapshotHistoryClick(object sender, RoutedEventArgs e) => ViewModel.OpenSnapshotHistoryCommand.Execute(null);

    private void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (sender is TextBox { DataContext: SessionViewModel session } &&
            session.ExecuteCommand.CanExecute(null))
        {
            session.ExecuteCommand.Execute(null);
            e.Handled = true;
        }
    }

    // 右键时选中鼠标所在项，使右键菜单操作作用于该项
    private void OnListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item != null)
            listBox.SelectedItem = item.DataContext;
    }

    // 仅在鼠标位于有效服务器条目上时才弹出右键菜单；空白区域不弹出
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var element = listBox.InputHitTest(Mouse.GetPosition(listBox)) as DependencyObject;
        if (FindAncestor<ListBoxItem>(element) == null)
            e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    // ---- 主页看板 / AI 快捷提问 ----

    private void OnHomeServersClick(object sender, MouseButtonEventArgs e) => ViewModel.NavigateServersCommand.Execute(null);

    private void OnHomeDatasourcesClick(object sender, MouseButtonEventArgs e) => ViewModel.NavigateDataSourcesCommand.Execute(null);

    private void OnHomeApplicationsClick(object sender, MouseButtonEventArgs e) => ViewModel.NavigateApplicationsCommand.Execute(null);

    private void OnHomeAddImage(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
            ViewModel.AddHomeAttachmentPaths(dialog.FileNames);
    }

    private void OnHomeAddFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加文件",
            Filter = "所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
            ViewModel.AddHomeAttachmentPaths(dialog.FileNames);
    }

    private void OnHomeClearAttachments(object sender, RoutedEventArgs e) => ViewModel.ClearHomeAttachments();

    private void OnHomeSend(object sender, RoutedEventArgs e) => ViewModel.SendHomeAsk();

    private void OnHomeAskKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) == 0)
        {
            ViewModel.SendHomeAsk();
            e.Handled = true;
        }
    }

    private void OnHomeMore(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is { } menu)
        {
            menu.PlacementTarget = element;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
