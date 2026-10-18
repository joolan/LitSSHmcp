using System.IO;
using System.Reflection;
using System.Threading;
using LitSSHmcp.App.Views;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 「安全设置 → Web 授权矩阵」UI 回归：XAML 能解析（新增 ItemsControl/DataTemplate/&lt; 转义的解析风险）
/// 且分组/绑定/事件处理器不被误删（可视化入口是授权矩阵的唯一配置界面，删了就回到手改 config.json）。
/// </summary>
public class SecuritySettingsAuthzXamlTests
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
    public void Security_settings_view_parses_xaml_with_authz_section()
    {
        var ex = RunSta(() => _ = new SecuritySettingsView());
        Assert.Null(ex);
    }

    [Fact]
    public void Authz_matrix_section_stays_in_security_settings_xaml()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "LitSSHmcp.App", "Views", "SecuritySettingsView.xaml"));

        Assert.Contains("Web 授权矩阵", source);
        Assert.Contains("Binding AuthzEnabled", source);
        Assert.Contains("Binding AuthzRules", source);
        Assert.Contains("OnAuthzAddRule", source);
        Assert.Contains("OnAuthzRemoveRule", source);
    }

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "LitSSHmcp.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("未找到仓库根（LitSSHmcp.slnx）");
    }
}
