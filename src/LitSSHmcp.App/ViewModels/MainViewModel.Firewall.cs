using System.Windows.Input;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<string, FirewallManagerWindow> _firewallWindows = new();

    private RelayCommand? _openFirewallManagerCommand;
    public ICommand OpenFirewallManagerCommand =>
        _openFirewallManagerCommand ??= new RelayCommand(_ => OpenFirewallManager(SelectedServer));

    private void OpenFirewallManager(SshServerConfig? server)
    {
        if (server is null)
            return;

        if (_firewallWindows.TryGetValue(server.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }

        var window = _windows.CreateFirewallManagerWindow(new FirewallManagerViewModel(
            server, _sshService, _dialogs, _services.CreateAuditLog()));
        window.Closed += (_, _) => _firewallWindows.Remove(server.Id);
        _firewallWindows[server.Id] = window;
        window.Show();
    }
}
