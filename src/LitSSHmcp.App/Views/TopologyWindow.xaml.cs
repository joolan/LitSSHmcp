using System.Windows;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class TopologyWindow : Window
{
    private readonly TopologyViewModel _viewModel = new();

    public TopologyWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _viewModel.Load();

    private void OnDiscover(object sender, RoutedEventArgs e) => _viewModel.Discover(ServerFilterBox.Text);
}
