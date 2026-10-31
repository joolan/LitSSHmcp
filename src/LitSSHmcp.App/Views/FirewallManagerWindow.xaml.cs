using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class FirewallManagerWindow : FluentWindow
{
    private readonly FirewallManagerViewModel _vm;

    public FirewallManagerWindow(FirewallManagerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        WindowLayout.Attach(this, "firewall-manager");
        Loaded += (_, _) => _ = _vm.RefreshAsync();
    }
}
