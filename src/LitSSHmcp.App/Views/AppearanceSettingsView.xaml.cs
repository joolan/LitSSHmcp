using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class AppearanceSettingsView : UserControl
{
    private readonly AppearanceSettingsViewModel _viewModel = new();

    public AppearanceSettingsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OnReload(object sender, RoutedEventArgs e) => _viewModel.Load();

    private void OnSave(object sender, RoutedEventArgs e) => _viewModel.Save();
}
