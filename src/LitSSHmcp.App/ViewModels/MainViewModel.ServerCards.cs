using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
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
        _editServerItemCommand ??= new RelayCommand(p => EditServer(p as SshServerConfig));

    private RelayCommand? _deleteServerItemCommand;
    public ICommand DeleteServerItemCommand =>
        _deleteServerItemCommand ??= new RelayCommand(p => DeleteServer(p as SshServerConfig));

    private void EditServer(SshServerConfig? server = null)
    {
        var fromItem = server != null;
        server ??= SelectedServer;
        if (server == null) return;
        if (fromItem && ReferenceEquals(SelectedServer, server))
            SelectedServer = null;

        var editWindow = _windows.CreateServerEditWindow(Application.Current.MainWindow);
        var editViewModel = new ServerEditViewModel(_configService, _sshService, editWindow, server);
        editWindow.DataContext = editViewModel;

        editViewModel.DialogClosed += (_, result) =>
        {
            editWindow.Close();
            if (result) LoadServers();
        };

        editWindow.ShowDialog();
    }

    private void DeleteServer(SshServerConfig? server = null) => AsyncOperation.Run("删除服务器", async () =>
    {
        var fromItem = server != null;
        server ??= SelectedServer;
        if (server == null) return;
        if (fromItem && ReferenceEquals(SelectedServer, server))
            SelectedServer = null;

        try
        {
            var config = await _configService.LoadConfigAsync();
            var nodeId = AssetNode.Ssh(server.Id);
            var references = RelationHelper.Referencing(config, nodeId);

            var message = $"确定要删除服务器 \"{server.Name}\" 吗？";
            message += references.Length > 0
                ? $"\n\n存在 {references.Length} 条关系记录引用它，将一并删除：\n{RelationHelper.Describe(references)}"
                : "\n\n（未发现引用它的关系记录）";

            if (!_dialogs.Confirm(message, "确认删除"))
                return;

            config.Servers = config.Servers.Where(s => s.Id != server.Id).ToArray();
            config.Relations = config.Relations.Where(r => r.From != nodeId && r.To != nodeId).ToArray();
            await _configService.SaveConfigAsync(config);

            StatusMessage = $"已删除服务器: {server.Name}";
            if (ReferenceEquals(SelectedServer, server))
                SelectedServer = null;
            LoadServers();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
        }
    });

}
