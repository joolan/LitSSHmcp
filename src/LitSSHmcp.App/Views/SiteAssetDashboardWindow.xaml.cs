using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class SiteAssetDashboardWindow : FluentWindow
{
    public SiteAssetDashboardWindow(SiteAssetDashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        WindowLayout.Attach(this, "site-asset-dashboard");
    }
}
