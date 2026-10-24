using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private bool _isServerCardView = string.Equals(
        UiPrefs.GetString("server.cardView", "false"), "true", StringComparison.OrdinalIgnoreCase);

    public bool IsServerCardView
    {
        get => _isServerCardView;
        set
        {
            if (_isServerCardView == value) return;
            _isServerCardView = value;
            UiPrefs.SetString("server.cardView", value ? "true" : "false");
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsServerListView));
        }
    }

    public bool IsServerListView => !IsServerCardView;

    private ICollectionView? _filteredServers;
    public ICollectionView FilteredServers
    {
        get
        {
            if (_filteredServers is not null)
                return _filteredServers;

            _filteredServers = new CollectionViewSource { Source = Servers }.View;
            _filteredServers.Filter = FilterServer;
            return _filteredServers;
        }
    }

    private RelayCommand? _openTerminalItemCommand;
    public ICommand OpenTerminalItemCommand =>
        _openTerminalItemCommand ??= new RelayCommand(p =>
        {
            SelectedServer = p as SshServerConfig;
            OpenTerminalCommand.Execute(null);
        });

    private RelayCommand? _openSftpItemCommand;
    public ICommand OpenSftpItemCommand =>
        _openSftpItemCommand ??= new RelayCommand(p =>
        {
            SelectedServer = p as SshServerConfig;
            OpenSftpManagerCommand.Execute(null);
        });

    private RelayCommand? _openMonitorItemCommand;
    public ICommand OpenMonitorItemCommand =>
        _openMonitorItemCommand ??= new RelayCommand(p =>
        {
            SelectedServer = p as SshServerConfig;
            OpenMonitorCommand.Execute(null);
        });

    private RelayCommand? _editServerItemCommand;
    public ICommand EditServerItemCommand =>
        _editServerItemCommand ??= new RelayCommand(p =>
        {
            SelectedServer = p as SshServerConfig;
            EditServerCommand.Execute(null);
        });

    private RelayCommand? _deleteServerItemCommand;
    public ICommand DeleteServerItemCommand =>
        _deleteServerItemCommand ??= new RelayCommand(p =>
        {
            SelectedServer = p as SshServerConfig;
            DeleteServerCommand.Execute(null);
        });
}
