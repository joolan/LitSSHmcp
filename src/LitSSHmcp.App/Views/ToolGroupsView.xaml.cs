using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class ToolGroupsView : UserControl
{
    private readonly ToolGroupsViewModel _viewModel = new();

    public ToolGroupsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OnReload(object sender, RoutedEventArgs e) => _viewModel.Load();

    private void OnSave(object sender, RoutedEventArgs e) => _viewModel.Save();

    private void OnEnableAll(object sender, RoutedEventArgs e) => _viewModel.EnableAll();

    private void OnDisableAll(object sender, RoutedEventArgs e) => _viewModel.DisableAll();
}
