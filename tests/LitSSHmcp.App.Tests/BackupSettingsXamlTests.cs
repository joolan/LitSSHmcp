using System.IO;
using System.Threading;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class BackupSettingsXamlTests
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
    public void Backup_settings_view_parses_xaml()
    {
        var ex = RunSta(() => _ = new BackupSettingsView());
        Assert.Null(ex);
    }

    [Fact]
    public void Backup_restore_and_history_windows_parse_xaml()
    {
        var ex = RunSta(() =>
        {
            var restore = new BackupRestoreWindow();
            restore.Close();
            var history = new BackupHistoryWindow(new BackupPlanConfig { Name = "test" });
            history.Close();
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Backup_plan_edit_window_parses_xaml()
    {
        var root = Path.Combine(Path.GetTempPath(), "litssh-backup-xaml-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = RunSta(() =>
            {
                var window = new BackupPlanEditWindow(null, new AppConfig(), root);
                window.Close();
            });
            Assert.Null(ex);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
