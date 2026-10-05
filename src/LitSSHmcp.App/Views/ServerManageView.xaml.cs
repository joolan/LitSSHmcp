using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class ServerManageView : UserControl
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public ServerManageView()
    {
        InitializeComponent();
    }

    private void OnConnectClick(object sender, RoutedEventArgs e) => ViewModel?.ConnectCommand.Execute(null);

    private void OnEditClick(object sender, RoutedEventArgs e) => ViewModel?.EditServerCommand.Execute(null);

    private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel?.DeleteServerCommand.Execute(null);

    private void OnSnapshotRefreshClick(object sender, RoutedEventArgs e) => ViewModel?.SnapshotRefreshCommand.Execute(null);

    private void OnSnapshotHistoryClick(object sender, RoutedEventArgs e) => ViewModel?.OpenSnapshotHistoryCommand.Execute(null);

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView listView)
            return;

        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item == null)
            return;

        listView.SelectedItem = item.DataContext;
        ViewModel?.ConnectCommand.Execute(null);
    }

    private void OnListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item != null)
            ServerManageList.SelectedItem = item.DataContext;
    }

    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListView listView)
            return;

        var element = listView.InputHitTest(Mouse.GetPosition(listView)) as DependencyObject;
        if (FindAncestor<ListViewItem>(element) == null)
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
}
