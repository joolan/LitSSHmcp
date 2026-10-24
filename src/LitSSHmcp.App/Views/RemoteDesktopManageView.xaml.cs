using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class RemoteDesktopManageView : UserControl
{
    private RemoteDesktopManageViewModel? ViewModel => DataContext as RemoteDesktopManageViewModel;

    public RemoteDesktopManageView()
    {
        InitializeComponent();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnSearchShortcut), true);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Dispatcher.BeginInvoke(new Action(FocusSearch), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        LitSSHmcp.App.Controls.ColumnChooser.Attach(RdList, "remoteDesktop.columns.v3");
    }

    private void OnConnectClick(object sender, RoutedEventArgs e) => ViewModel?.ConnectCommand.Execute(null);

    private void OnOpenExternalClick(object sender, RoutedEventArgs e) => ViewModel?.OpenExternalCommand.Execute(null);

    private void OnEditClick(object sender, RoutedEventArgs e) => ViewModel?.EditCommand.Execute(null);

    private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel?.DeleteCommand.Execute(null);

    private void OnChooseColumns(object sender, RoutedEventArgs e)
        => LitSSHmcp.App.Controls.ColumnChooser.Show((FrameworkElement)sender, RdList, "remoteDesktop.columns.v3");

    private void OnSearchShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Header.IsKeyboardFocusWithin)
        {
            ClearSearch();
            e.Handled = true;
        }
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) => ClearSearch();

    private void ClearSearch()
    {
        if (ViewModel is { } vm)
            vm.SearchText = string.Empty;
        FocusSearch();
    }

    private void FocusSearch()
    {
        Header.FocusFirstInput();
        
    }

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
            RdList.SelectedItem = item.DataContext;
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
