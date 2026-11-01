using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class JenkinsView : UserControl
{
    public JenkinsView()
    {
        InitializeComponent();
    }

    public JenkinsViewModel? ViewModel => DataContext as JenkinsViewModel;

    private void OnJobSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is JenkinsViewModel viewModel)
            viewModel.SelectedJob = e.NewValue as JenkinsJobNode;
    }

    private void OnJobExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: JenkinsJobNode node })
            AsyncOperation.Run("加载 Jenkins 子任务", node.EnsureChildrenAsync);
    }

    private void OnParameterPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: JenkinsParameterField field } passwordBox)
            field.Value = passwordBox.Password;
    }

    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is JenkinsViewModel { IsLogFollow: true } && sender is TextBox textBox)
            textBox.ScrollToEnd();
    }

    private void OnBuildHistoryPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = FindAncestor<ScrollViewer>(sender as DependencyObject);
        if (scrollViewer is null)
            return;

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is T match)
                return match;
        }

        return null;
    }
}
