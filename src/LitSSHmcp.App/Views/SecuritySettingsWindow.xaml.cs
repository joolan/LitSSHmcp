using System.Windows;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class SecuritySettingsWindow : Window
{
    private readonly SecuritySettingsViewModel _viewModel = new();

    public SecuritySettingsWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        HostKeyModeBox.ItemsSource = new[] { "Tofu", "Strict", "Off" };
        ApprovalStyleBox.ItemsSource = new[] { "process", "dialog", "native" };
    }

    private void OnReload(object sender, RoutedEventArgs e) => _viewModel.Load();

    private void OnSave(object sender, RoutedEventArgs e) => _viewModel.Save();
}
