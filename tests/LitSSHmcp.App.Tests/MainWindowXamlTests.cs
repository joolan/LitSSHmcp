using System.Threading;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 冒烟测试：构造主窗口、设置窗口及主区域/设置页 UserControl，确保 XAML 能成功解析。
/// </summary>
public class MainWindowXamlTests
{
    private static Exception? RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }

    [Fact]
    public void Main_window_parses_xaml()
    {
        var ex = RunSta(() => _ = new MainWindow());
        Assert.Null(ex);
    }

    [Fact]
    public void Main_window_pages_do_not_overlap()
    {
        var ex = RunSta(() =>
        {
            var window = new MainWindow();
            var vm = (LitSSHmcp.App.ViewModels.MainViewModel)window.DataContext;
            var home = (System.Windows.UIElement)window.FindName("HomeView")!;
            var servers = (System.Windows.UIElement)window.FindName("ServersView")!;
            var dataSources = (System.Windows.UIElement)window.FindName("DataSourcesView")!;
            var applications = (System.Windows.UIElement)window.FindName("ApplicationsView")!;

            window.UpdateLayout();
            Pump(window);
            Assert.Equal(System.Windows.Visibility.Visible, home.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, servers.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, dataSources.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, applications.Visibility);

            vm.CurrentPage = LitSSHmcp.App.ViewModels.MainPage.Applications;
            window.UpdateLayout();
            Pump(window);
            Assert.Equal(System.Windows.Visibility.Collapsed, home.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, servers.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, dataSources.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, applications.Visibility);

            vm.CurrentPage = LitSSHmcp.App.ViewModels.MainPage.Servers;
            window.UpdateLayout();
            Pump(window);
            Assert.Equal(System.Windows.Visibility.Collapsed, home.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, servers.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, dataSources.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, applications.Visibility);
        });
        Assert.Null(ex);
    }

    private static void Pump(System.Windows.Window window)
        => window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);

    [Fact]
    public void App_settings_window_parses_xaml()
    {
        var ex = RunSta(() => _ = new AppSettingsWindow(new ConfigService()));
        Assert.Null(ex);
    }

    [Fact]
    public void Main_area_views_parse_xaml()
    {
        var ex = RunSta(() =>
        {
            _ = new ServerManageView();
            _ = new DatasourceManageView();
            _ = new ApplicationManageView();
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Settings_views_parse_xaml()
    {
        var ex = RunSta(() =>
        {
            _ = new SecuritySettingsView();
            _ = new ToolGroupsView();
            _ = new ConfigTransferView();
            _ = new McpToolsView();
        });
        Assert.Null(ex);
    }
}
