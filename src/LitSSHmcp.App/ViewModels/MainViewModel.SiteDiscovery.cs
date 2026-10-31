using System.Windows.Input;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<string, SiteDiscoveryWindow> _siteDiscoveryWindows = new();

    private RelayCommand? _openSiteDiscoveryCommand;
    public ICommand OpenSiteDiscoveryCommand =>
        _openSiteDiscoveryCommand ??= new RelayCommand(_ => OpenSiteDiscovery(SelectedServer),
            _ => SelectedServer is not null);

    private void OpenSiteDiscovery(SshServerConfig? server)
    {
        if (server is null)
            return;

        if (_siteDiscoveryWindows.TryGetValue(server.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }

        var window = _windows.CreateSiteDiscoveryWindow(new SiteDiscoveryViewModel(
            server, _sshService, _dialogs, _services.CreateAuditLog()));
        window.Closed += (_, _) => _siteDiscoveryWindows.Remove(server.Id);
        _siteDiscoveryWindows[server.Id] = window;
        window.Show();
    }
}
