using System.Text;
using System.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Scheduling;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ScheduledTasksWindowXamlTests
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
    public void Scheduled_task_windows_parse_xaml()
    {
        var ex = RunSta(() =>
        {
            var server = new SshServerConfig { Name = "test", Host = "127.0.0.1" };
            var viewModel = new ScheduledTasksViewModel(
                server,
                AppServiceFactory.CreateSshService(),
                new UserDialogService());
            _ = new ScheduledTasksWindow(viewModel);

            var context = new ScriptEditContext
            {
                Task = new ScheduledTaskInfo { Id = "test", ScriptPath = "/tmp/demo.sh" },
                FilePath = "/tmp/demo.sh",
                Content = "echo hello\n",
                Meta = "root:root · 权限 755",
                Interpreter = "bash",
                Encoding = Encoding.UTF8,
                SaveAsync = _ => Task.FromResult(new ScriptSaveResult(true, "ok"))
            };
            _ = new RemoteScriptEditorWindow(context);
            _ = new ScheduledTaskAiPromptWindow("demo prompt", (_, _) => Task.FromResult(true));
        });

        Assert.Null(ex);
    }
}
