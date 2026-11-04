using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class HomeDashboardView : UserControl
{
    private HomeDashboardViewModel? _dashboard;

    public HomeDashboardView()
    {
        InitializeComponent();
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += (_, _) => Hook();
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        Hook();
        if (IsVisible)
            _dashboard?.Start();
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e) => _dashboard?.Stop();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            _dashboard?.Start();
        else
            _dashboard?.Stop();
    }

    private void Hook()
    {
        var dashboard = (DataContext as MainViewModel)?.HomeDashboard;
        if (ReferenceEquals(_dashboard, dashboard))
            return;

        _dashboard?.Stop();
        _dashboard = dashboard;
        if (IsLoaded && IsVisible)
            _dashboard?.Start();
    }
}
