using System.IO;
using System.Reflection;
using System.Threading;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 冒烟测试：构造应用锁窗口 / 密码对话框，确保 XAML 能成功解析（防止运行时 XamlParseException 回归）。
/// </summary>
public class AppLockWindowXamlTests
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

    private static AppLockService CreateLockService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-lock-xaml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new AppLockService(new ConfigService(Path.Combine(dir, "config.json"), new DpapiSecretProtector()));
    }

    [Fact]
    public void Lock_window_allows_minimize_and_taskbar_restore()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "LitSSHmcp.App", "Views", "AppLockWindow.xaml"));

        Assert.Contains("ShowMinimize=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar=\"True\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_password_mode_parses_xaml()
    {
        var ex = RunSta(() => _ = new AppLockWindow(CreateLockService(), AppLockWindowMode.SetPassword, relock: false));
        Assert.Null(ex);
    }

    [Fact]
    public void Unlock_mode_parses_xaml()
    {
        var ex = RunSta(() => _ = new AppLockWindow(CreateLockService(), AppLockWindowMode.Unlock, relock: true));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(AppLockPasswordDialogMode.Set)]
    [InlineData(AppLockPasswordDialogMode.Change)]
    [InlineData(AppLockPasswordDialogMode.Verify)]
    public void Password_dialog_parses_xaml(AppLockPasswordDialogMode mode)
    {
        // Show()/ShowDialog 会阻塞测试线程，反射调用私有构造完成 XAML 解析验证
        var ctor = typeof(AppLockPasswordDialog).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(AppLockService), typeof(AppLockPasswordDialogMode) }, null);
        Assert.NotNull(ctor);

        var ex = RunSta(() => _ = ctor!.Invoke(new object[] { CreateLockService(), mode }));
        Assert.Null(ex);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LitSSHmcp.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("未找到仓库根目录(含 LitSSHmcp.slnx)");
    }
}
