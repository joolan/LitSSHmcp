using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class DatasourceManageWindow : Window
{
    private DatasourceManageViewModel ViewModel => (DatasourceManageViewModel)DataContext;

    public DatasourceManageWindow()
    {
        InitializeComponent();
        DataContext = new DatasourceManageViewModel(new LitSSHmcp.Core.Services.Storage.ConfigService());
    }

    private void OnTestClick(object sender, RoutedEventArgs e) => ViewModel.TestCommand.Execute(null);

    private void OnEditClick(object sender, RoutedEventArgs e) => ViewModel.EditCommand.Execute(null);

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 仅在双击到有效的条目时编辑；空白区域双击不触发
        if (sender is not ListView listView)
            return;

        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item == null)
            return;

        listView.SelectedItem = item.DataContext;
        ViewModel.EditCommand.Execute(null);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel.DeleteCommand.Execute(null);

    private void OnListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item != null)
            DsList.SelectedItem = item.DataContext;
    }

    // 仅在鼠标位于有效条目上时才弹出右键菜单；空白区域不弹出
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
