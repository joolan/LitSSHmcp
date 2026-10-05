using System.IO;
using System.Threading;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 冒烟测试：构造 AI 助手窗口与设置窗口，确保 XAML 能成功解析（防止 ContextMenu/连接 ID 之类的运行时 XamlParseException 回归）。
/// </summary>
public class AgentWindowXamlTests
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
    public void Agent_settings_window_parses_xaml()
    {
        var ex = RunSta(() => _ = new AgentSettingsWindow(new ConfigService()));
        Assert.Null(ex);
    }

    [Fact]
    public void Agent_window_parses_xaml()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-xaml-" + Guid.NewGuid().ToString("N") + ".db");
        Exception? ex = null;
        try
        {
            ex = RunSta(() => _ = new AgentWindow(new ConfigService(), new ContextStore(db), "skills"));
        }
        finally
        {
            try { File.Delete(db); } catch { /* ignore */ }
        }
        Assert.Null(ex);
    }
}
