using System.Windows.Input;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<string, UserManagerWindow> _userManagerWindows = new();

    private RelayCommand? _openUserManagerCommand;
    public ICommand OpenUserManagerCommand =>
        _openUserManagerCommand ??= new RelayCommand(_ => OpenUserManager(SelectedServer),
            _ => SelectedServer is not null);

    private void OpenUserManager(SshServerConfig? server)
    {
        if (server is null)
            return;

        if (_userManagerWindows.TryGetValue(server.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }

        var window = _windows.CreateUserManagerWindow(new UserManagerViewModel(
            server, _sshService, _dialogs, _services.CreateAuditLog()));
        window.Closed += (_, _) => _userManagerWindows.Remove(server.Id);
        _userManagerWindows[server.Id] = window;
        window.Show();
    }
}
