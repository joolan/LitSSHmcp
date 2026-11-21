using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private ServerManageView? _serverPageView;
    public ServerManageView? ServerPageView
    {
        get => _serverPageView;
        private set { _serverPageView = value; OnPropertyChanged(); }
    }

    private FileTransferManageView? _fileTransferPageView;
    public FileTransferManageView? FileTransferPageView
    {
        get => _fileTransferPageView;
        private set { _fileTransferPageView = value; OnPropertyChanged(); }
    }

    private DatasourceManageView? _datasourcePageView;
    public DatasourceManageView? DatasourcePageView
    {
        get => _datasourcePageView;
        private set { _datasourcePageView = value; OnPropertyChanged(); }
    }

    private ApplicationManageView? _applicationPageView;
    public ApplicationManageView? ApplicationPageView
    {
        get => _applicationPageView;
        private set { _applicationPageView = value; OnPropertyChanged(); }
    }

    private RemoteDesktopManageView? _remoteDesktopPageView;
    public RemoteDesktopManageView? RemoteDesktopPageView
    {
        get => _remoteDesktopPageView;
        private set { _remoteDesktopPageView = value; OnPropertyChanged(); }
    }

    private void NavigateServers()
    {
        CurrentPage = MainPage.Servers;
        ServerPageView ??= new ServerManageView { DataContext = this };
    }

    private void EnsureFileTransferPageView()
        => FileTransferPageView ??= new FileTransferManageView { DataContext = FileTransferPage };

    private void EnsureDatasourcePageView()
        => DatasourcePageView ??= new DatasourceManageView { DataContext = DatasourcePage };

    private void EnsureApplicationPageView()
        => ApplicationPageView ??= new ApplicationManageView { DataContext = ApplicationPage };

    private void EnsureRemoteDesktopPageView()
        => RemoteDesktopPageView ??= new RemoteDesktopManageView { DataContext = RemoteDesktopPage };
}
