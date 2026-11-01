using System.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ResourceAnalysisWindowXamlTests
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
    public void Resource_analysis_windows_parse_xaml()
    {
        var ex = RunSta(() =>
        {
            var server = new SshServerConfig { Name = "test", Host = "127.0.0.1" };
            var ssh = AppServiceFactory.CreateSshService();
            var dialogs = new UserDialogService();
            var viewModel = new ResourceAnalysisViewModel(server, ssh, dialogs);
            _ = new ResourceAnalysisWindow(viewModel);
            _ = new ServerMonitorWindow(new ServerMonitorViewModel(
                server,
                ssh,
                _ => Task.CompletedTask,
                _ => Task.CompletedTask));
        });

        Assert.Null(ex);
    }
}
