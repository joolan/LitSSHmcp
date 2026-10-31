using System.Windows.Input;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private SiteAssetDashboardWindow? _siteAssetDashboardWindow;

    private RelayCommand? _openSiteAssetsCommand;
    public ICommand OpenSiteAssetsCommand =>
        _openSiteAssetsCommand ??= new RelayCommand(_ => OpenSiteAssets());

    private void OpenSiteAssets()
    {
        if (_siteAssetDashboardWindow is { IsLoaded: true })
        {
            _siteAssetDashboardWindow.Activate();
            return;
        }

        var window = _windows.CreateSiteAssetDashboardWindow(
            new SiteAssetDashboardViewModel(_dialogs, _configService, _sshService, _services.CreateAuditLog(), _windows));
        window.Closed += (_, _) => _siteAssetDashboardWindow = null;
        _siteAssetDashboardWindow = window;
        window.Show();
    }
}
