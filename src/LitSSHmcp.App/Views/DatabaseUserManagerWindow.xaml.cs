using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class DatabaseUserManagerWindow : FluentWindow
{
    private readonly DatabaseUserManagerViewModel _viewModel;

    public DatabaseUserManagerWindow(DatabaseUserManagerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        WindowLayout.Attach(this, "db-user-manager");
        Loaded += (_, _) => viewModel.RefreshCommand.Execute(null);
    }

    private void OnChangePassword(object sender, RoutedEventArgs e) => _ = ChangePasswordAsync();

    private async Task ChangePasswordAsync()
    {
        await _viewModel.ChangePasswordAsync(ChangePasswordBox.Password);
        ChangePasswordBox.Clear();
    }

    private void OnOpenCreateUser(object sender, RoutedEventArgs e)
    {
        var dialog = new DatabaseCreateUserWindow { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        _ = _viewModel.CreateUserAsync(dialog.UserName, dialog.Host, dialog.Password, dialog.CanLogin);
    }
}
