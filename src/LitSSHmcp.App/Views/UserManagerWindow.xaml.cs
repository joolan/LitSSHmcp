using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class UserManagerWindow : FluentWindow
{
    public UserManagerWindow(UserManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        WindowLayout.Attach(this, "user-manager");
        Loaded += (_, _) => viewModel.RefreshCommand.Execute(null);
    }
}
