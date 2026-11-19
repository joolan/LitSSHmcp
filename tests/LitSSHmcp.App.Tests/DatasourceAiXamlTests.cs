using System.Threading;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Views;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class DatasourceAiXamlTests
{
    [Fact]
    public void Drawer_parses_xaml()
    {
        var ex = RunSta(() => _ = new DatasourceAiDrawer());
        Assert.Null(ex);
    }

    [Fact]
    public void Sql_confirm_window_parses_xaml()
    {
        var ex = RunSta(() => _ = new DatasourceAiSqlConfirmWindow(
            "SELECT * FROM orders", "Shop · shop", "每次 AI 生成的只读查询都需要确认。"));
        Assert.Null(ex);
    }

    [Fact]
    public void Send_result_window_parses_xaml()
    {
        var ex = RunSta(() => _ = new DatasourceAiSendResultWindow(30, 5, "SELECT * FROM orders"));
        Assert.Null(ex);
    }

    [Fact]
    public void Session_manager_window_parses_xaml()
    {
        var ex = RunSta(() => _ = new DatasourceAiSessionManagerWindow(
            "ds1", "mysql", "shop", 1));
        Assert.Null(ex);
    }

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
}
