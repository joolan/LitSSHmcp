using System.Windows.Input;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<string, DockerManagerWindow> _dockerWindows = new();

    private RelayCommand? _openDockerManagerCommand;
    public ICommand OpenDockerManagerCommand =>
        _openDockerManagerCommand ??= new RelayCommand(_ => OpenDockerManager(SelectedServer));

    private void OpenDockerManager(SshServerConfig? server)
    {
        if (server is null)
            return;

        if (_dockerWindows.TryGetValue(server.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }

        var window = _windows.CreateDockerManagerWindow(new DockerManagerViewModel(
            server, _sshService, _dialogs, _services.CreateAuditLog()));
        window.Closed += (_, _) => _dockerWindows.Remove(server.Id);
        _dockerWindows[server.Id] = window;
        window.Show();
    }
}
