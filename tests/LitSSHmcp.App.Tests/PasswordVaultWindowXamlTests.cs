using System.IO;
using System.Threading;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.Views.Tools;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class PasswordVaultWindowXamlTests
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
    public void Password_vault_window_parses_xaml()
    {
        var root = Path.Combine(Path.GetTempPath(), "litssh-pv-xaml-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = RunSta(() =>
            {
                var window = new PasswordVaultWindow(new PasswordVaultService(root, iterations: 1000));
                window.Close();
            });
            Assert.Null(ex);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Password_item_edit_window_parses_xaml()
    {
        var ex = RunSta(() =>
        {
            var window = new PasswordItemEditWindow();
            window.Close();
        });
        Assert.Null(ex);
    }
}
