using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LitSSHmcp.App.ViewModels;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views;

public partial class JenkinsView : UserControl
{
    private JenkinsViewModel? _subscribedViewModel;
    private bool _resetBuildHistoryScroll;

    public JenkinsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
    }

    public JenkinsViewModel? ViewModel => DataContext as JenkinsViewModel;

    private void AttachViewModel()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel.Builds.CollectionChanged -= OnBuildsCollectionChanged;
        }

        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is null)
            return;

        _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _subscribedViewModel.Builds.CollectionChanged += OnBuildsCollectionChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JenkinsViewModel.SelectedJob))
            return;

        _resetBuildHistoryScroll = true;
        Dispatcher.BeginInvoke(new Action(ResetBuildHistoryScroll), DispatcherPriority.ContextIdle);
    }

    private void OnBuildsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_resetBuildHistoryScroll || BuildHistoryList.Items.Count == 0)
            return;

        _resetBuildHistoryScroll = false;
        Dispatcher.BeginInvoke(new Action(ResetBuildHistoryScroll), DispatcherPriority.ContextIdle);
    }

    private void ResetBuildHistoryScroll()
    {
        FindAncestor<ScrollViewer>(BuildHistoryList)?.ScrollToTop();
        if (BuildHistoryList.Items.Count == 0)
            return;

        BuildHistoryList.ScrollIntoView(BuildHistoryList.Items[0]);
        FindChild<ScrollViewer>(BuildHistoryList)?.ScrollToTop();
    }

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

    private static T? FindChild<T>(DependencyObject? current) where T : DependencyObject
    {
        if (current is null)
            return null;

        var count = VisualTreeHelper.GetChildrenCount(current);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(current, i);
            if (child is T match)
                return match;

            var nested = FindChild<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
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
