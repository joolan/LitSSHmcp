using System.Diagnostics;
using System.Windows;
using LitSSHmcp.App.Services;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>网络恢复/断开：当前页是 Jenkins/K8s 时自动恢复或暂停后台刷新。</summary>
    public void ApplyNetworkAvailability(bool available)
    {
        if (available)
        {
            if (CurrentPage == MainPage.Jenkins && JenkinsPage is not null)
                AsyncOperation.Run("网络恢复，刷新 Jenkins", JenkinsPage.ActivateAsync);
            else if (CurrentPage == MainPage.Kubernetes && KubernetesPage is not null)
                AsyncOperation.Run("网络恢复，刷新 Kubernetes", KubernetesPage.ActivateAsync);
            return;
        }

        if (CurrentPage == MainPage.Jenkins)
            JenkinsPage?.SetActive(false);
        else if (CurrentPage == MainPage.Kubernetes)
            KubernetesPage?.SetActive(false);
        StatusMessage = "网络已断开，已暂停后台刷新。";
    }

    /// <summary>离开页面时停止该页面的事件轮询/取消在途请求，避免后台继续占用网络与 UI 线程。</summary>
    private void DeactivatePage(MainPage page)
    {
        switch (page)
        {
            case MainPage.Jenkins:
                JenkinsPage?.SetActive(false);
                break;
            case MainPage.Kubernetes:
                KubernetesPage?.SetActive(false);
                break;
        }
    }

    // 数据源管理：主区域页面
    private DatasourceManageViewModel? _datasourcePage;
    public DatasourceManageViewModel? DatasourcePage
    {
        get => _datasourcePage;
        private set { _datasourcePage = value; OnPropertyChanged(); }
    }

    private void NavigateDataSources()
    {
        CurrentPage = MainPage.DataSources;
        DatasourcePage ??= CreateDatasourcePage();
        DatasourcePage.RefreshCommand.Execute(null);
    }

    // 应用管理：主区域页面
    private ApplicationManageViewModel? _applicationPage;
    public ApplicationManageViewModel? ApplicationPage
    {
        get => _applicationPage;
        private set { _applicationPage = value; OnPropertyChanged(); }
    }

    private void NavigateApplications()
    {
        CurrentPage = MainPage.Applications;
        ApplicationPage ??= new ApplicationManageViewModel(_configService, Application.Current.MainWindow);
        ApplicationPage.RefreshCommand.Execute(null);
    }

    // 远程桌面（RDP / VNC）：主区域页面
    private RemoteDesktopManageViewModel? _remoteDesktopPage;
    public RemoteDesktopManageViewModel? RemoteDesktopPage
    {
        get => _remoteDesktopPage;
        private set { _remoteDesktopPage = value; OnPropertyChanged(); }
    }

    private void NavigateRemoteDesktops()
    {
        CurrentPage = MainPage.RemoteDesktops;
        RemoteDesktopPage ??= new RemoteDesktopManageViewModel(_configService, services: _services);
        RemoteDesktopPage.RefreshCommand.Execute(null);
    }

    // Jenkins：主区域页面；连接数量不设上限，环境只是标签
    private JenkinsViewModel? _jenkinsPage;
    public JenkinsViewModel? JenkinsPage
    {
        get => _jenkinsPage;
        private set { _jenkinsPage = value; OnPropertyChanged(); }
    }

    private void NavigateJenkins()
    {
        var sw = Stopwatch.StartNew();
        CurrentPage = MainPage.Jenkins;   // 先切页，页面立即获得焦点；加载在后台进行
        PerfLog.Write($"nav.jenkins switch={sw.Elapsed.TotalMilliseconds:0.0}ms");
        ClearJenkinsNotifications();

        if (JenkinsPage is not null)
        {
            AsyncOperation.Run("激活 Jenkins", JenkinsPage.ActivateAsync);
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (CurrentPage != MainPage.Jenkins)
                return;

            if (JenkinsPage is null)
            {
                JenkinsPage = new JenkinsViewModel(_configService, Application.Current.MainWindow, _dialogs, _windows,
                    _services.CreateAuditLog());
                JenkinsPage.BuildCompleted += OnJenkinsBuildCompleted;
            }

            AsyncOperation.Run("激活 Jenkins", JenkinsPage.ActivateAsync);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void DisposeJenkinsPage() => JenkinsPage?.Dispose();

    private KubernetesViewModel? _kubernetesPage;
    public KubernetesViewModel? KubernetesPage
    {
        get => _kubernetesPage;
        private set { _kubernetesPage = value; OnPropertyChanged(); }
    }

    private void NavigateKubernetes()
    {
        var sw = Stopwatch.StartNew();
        CurrentPage = MainPage.Kubernetes;   // 先切页，页面立即获得焦点；加载在后台进行
        PerfLog.Write($"nav.kubernetes switch={sw.Elapsed.TotalMilliseconds:0.0}ms");

        if (KubernetesPage is not null)
        {
            AsyncOperation.Run("激活 Kubernetes", KubernetesPage.ActivateAsync);
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (CurrentPage != MainPage.Kubernetes)
                return;

            if (KubernetesPage is null)
            {
                KubernetesPage = new KubernetesViewModel(
                    _configService, _windows, _sshService, _dialogs, Application.Current.MainWindow,
                    _services.CreateAuditLog());
            }

            AsyncOperation.Run("激活 Kubernetes", KubernetesPage.ActivateAsync);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void DisposeKubernetesPage() => KubernetesPage?.Dispose();
}
