using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class SiteDiscoveryWindow : FluentWindow
{
    public SiteDiscoveryWindow(SiteDiscoveryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        WindowLayout.Attach(this, "site-discovery");
    }
}
