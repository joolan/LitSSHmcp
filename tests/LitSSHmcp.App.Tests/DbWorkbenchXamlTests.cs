using System.Threading;
using LitSSHmcp.App.Views;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>冒烟测试：构造数据库工作台窗口，确保 XAML（含 AvalonEdit 编辑器）能成功解析。</summary>
public class DbWorkbenchXamlTests
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
    public void Db_workbench_window_parses_xaml()
    {
        var ex = RunSta(() => _ = new DbWorkbenchWindow());
        Assert.Null(ex);
    }
}
