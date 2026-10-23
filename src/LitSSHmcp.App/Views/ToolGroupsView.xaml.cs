using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class ToolGroupsView : UserControl
{
    private ToolGroupsViewModel _viewModel = new();

    public ToolGroupsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    public void Initialize(IAppServices? services)
    {
        _viewModel = new ToolGroupsViewModel(services?.Config);
        DataContext = _viewModel;
    }

    private void OnReload(object sender, RoutedEventArgs e) => _viewModel.Load();

    private void OnSave(object sender, RoutedEventArgs e) => _viewModel.Save();

    private void OnEnableAll(object sender, RoutedEventArgs e) => _viewModel.EnableAll();

    private void OnDisableAll(object sender, RoutedEventArgs e) => _viewModel.DisableAll();
}
