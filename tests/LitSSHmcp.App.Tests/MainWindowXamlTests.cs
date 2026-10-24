using System.Reflection;
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
            _ = new SshSessionWindow();
            _ = new TopologyWindow();
            _ = new AuditWindow();
            _ = new ServerEditWindow();
            _ = new DatasourceEditWindow();
            _ = new RemoteDesktopEditWindow();
            _ = new DbWritePreviewWindow(new LitSSHmcp.App.ViewModels.WritePreviewInfo
            {
                Sql = "UPDATE demo SET value = 1 WHERE id = 2",
                DataSourceName = "demo",
                DatabaseName = "test",
                AffectedRows = 1
            });
            _ = new LitSSHmcp.App.Controls.RemoteFileBrowserView();
            _ = new LitSSHmcp.App.Controls.TerminalTabView();
            var server = new LitSSHmcp.Core.Models.SshServerConfig { Name = "t", Host = "127.0.0.1" };
            var vm = new LitSSHmcp.App.ViewModels.RemoteFileBrowserViewModel(server, new LitSSHmcp.Core.Services.SSH.SshService());
            _ = new RemoteFilePropertiesWindow(vm, new LitSSHmcp.App.ViewModels.RemoteFileItem { Name = "a.txt", FullName = "/home/u/a.txt" });
            _ = new BatchExecWindow();
            _ = new TerminalLogWindow();
            _ = new ServerMonitorWindow(new LitSSHmcp.App.ViewModels.ServerMonitorViewModel(server, new LitSSHmcp.Core.Services.SSH.SshService()));
            _ = new ServerSnippetsWindow(server);
            _ = new SftpManagerWindow(new LitSSHmcp.App.ViewModels.SftpManagerViewModel(server, new LitSSHmcp.Core.Services.SSH.SshService()));
            _ = new ConflictDialog("a.txt");
            _ = new SessionBatchExecWindow();
            _ = new OpenWithDialog("a.txt");
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Topology_inspector_follows_selection()
    {
        var ex = RunSta(() =>
        {
            var window = new TopologyWindow();
            var vm = (LitSSHmcp.App.ViewModels.TopologyViewModel)window.DataContext;
            var inspector = (System.Windows.UIElement)window.FindName("NodeInspector")!;

            Pump(window);
            Assert.Equal(System.Windows.Visibility.Collapsed, inspector.Visibility);

            vm.SelectNode("demo-node");
            Pump(window);
            Assert.Equal(System.Windows.Visibility.Visible, inspector.Visibility);

            vm.ClearSelection();
            Pump(window);
            Assert.Equal(System.Windows.Visibility.Collapsed, inspector.Visibility);
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Audit_detail_drawer_opens_on_row_selection()
    {
        var ex = RunSta(() =>
        {
            var window = new AuditWindow();
            var vm = (LitSSHmcp.App.ViewModels.AuditViewModel)window.DataContext;
            var list = (System.Windows.Controls.ListView)window.FindName("CommandList")!;
            var drawer = (System.Windows.UIElement)window.FindName("AuditDetailDrawer")!;
            var log = new LitSSHmcp.Core.Models.CommandAuditLog
            {
                Timestamp = DateTime.UtcNow,
                Tool = "ssh_execute",
                ServerName = "web01",
                Command = "uptime",
                Status = LitSSHmcp.Core.Models.CommandStatus.Executed
            };

            window.Show();
            Pump(window);
            vm.CommandLogs.Add(log);
            Pump(window);
            Assert.Equal(System.Windows.Visibility.Collapsed, drawer.Visibility);

            list.SelectedItem = log;
            Pump(window);
            Assert.Equal(System.Windows.Visibility.Visible, drawer.Visibility);
            Assert.Same(log, vm.SelectedAuditItem);
            window.Close();
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Command_palette_parses_xaml()
    {
        var ex = RunSta(() => _ = new CommandPaletteWindow(new LitSSHmcp.App.ViewModels.MainViewModel()));
        Assert.Null(ex);
    }

    [Fact]
    public void Form_controls_parse_xaml()
    {
        var ex = RunSta(() =>
        {
            _ = new LitSSHmcp.App.Controls.FormRow();
            _ = new LitSSHmcp.App.Controls.PathPicker();
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
            _ = new AppearanceSettingsView();
            _ = new TerminalSettingsView();
            _ = new SnippetEditorView();
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Log_dialogs_parse_xaml()
    {
        var ex = RunSta(() =>
        {
            var sliceCtor = typeof(LogSliceDialog).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(string) }, null);
            Assert.NotNull(sliceCtor);
            _ = sliceCtor.Invoke(new object[] { "/var/log/a.log", "/var/log/a.slice.log" });

            var searchCtor = typeof(LogSearchDialog).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(bool) }, null);
            Assert.NotNull(searchCtor);
            _ = searchCtor.Invoke(new object[] { "搜索日志", true });
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Remote_desktop_and_about_views_parse_xaml()
    {
        var ex = RunSta(() =>
        {
            _ = new RemoteDesktopManageView();
            _ = new AboutView();
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Tunnel_mark_shows_check_only_when_tunnel_configured()
    {
        var conv = LitSSHmcp.App.Controls.TunnelMarkConverter.Instance;
        Assert.Equal("√", conv.Convert("server-1", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, conv.Convert("", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, conv.Convert("   ", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, conv.Convert(null, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }
}
