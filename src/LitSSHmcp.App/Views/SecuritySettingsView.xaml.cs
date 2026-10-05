using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class SecuritySettingsView : UserControl
{
    private readonly SecuritySettingsViewModel _viewModel = new();

    public SecuritySettingsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        HostKeyModeBox.ItemsSource = new[] { "Tofu", "Strict", "Off" };
        ApprovalStyleBox.ItemsSource = new[] { "process", "dialog", "native" };
    }

    private void OnReload(object sender, RoutedEventArgs e) => _viewModel.Load();

    private void OnSave(object sender, RoutedEventArgs e) => _viewModel.Save();
}
