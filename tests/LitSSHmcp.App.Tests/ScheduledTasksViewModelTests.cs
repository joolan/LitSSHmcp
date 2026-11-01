using System.Text;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Scheduling;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ScheduledTasksViewModelTests
{
    private const string DirectProbe = """
__LITSSH_META__
uname=Linux
hostname=web01
user=deploy
uid=1000
date=2026-01-01T10:00:00+0800
os=Debian GNU/Linux 12
kernel=6.1.0
crontab=/usr/bin/crontab
systemctl=/usr/bin/systemctl
cron_state=active
__LITSSH_CURRENT_CRONTAB__
0 3 * * * /opt/backup.sh
#LITSSH_PAUSED# */5 * * * * /opt/check.sh
__LITSSH_ETC_CRONTAB__
17 * * * * root cd / && run-parts /etc/cron.hourly
__LITSSH_CRON_D__
###FILE:/etc/cron.d/app
*/10 * * * * app /opt/app.sh
__LITSSH_CRON_DIRS__
###FILE:/etc/cron.daily/logrotate|755|root|root|1234|1700000000
__LITSSH_TIMERS__
###TIMER:backup.timer
Id=backup.timer
Description=Daily backup
Unit=backup.service
ActiveState=active
SubState=waiting
UnitFileState=enabled
NextElapseUSecRealtime=0
LastTriggerUSec=0
TimersCalendar={ OnCalendar=*-*-* 03:00:00 ; next_elapse=0 }
TimersMonotonic=
FragmentPath=/etc/systemd/system/backup.timer
###TIMER_SERVICE:backup.service
Id=backup.service
Description=Daily backup service
User=root
WorkingDirectory=/opt
ExecStart={ path=/bin/bash ; argv[]=/bin/bash /opt/backup.sh ; ignore_errors=no ; start_time=[n/a] ; stop_time=[n/a] ; pid=0 ; code=(null) ; status=0/0 }
FragmentPath=/etc/systemd/system/backup.service
__LITSSH_END__
""";

    private const string SudoProbe = """
__LITSSH_SUDO_META__
root
__LITSSH_SUDO_CRONTAB__
0 4 * * * /root/nightly.sh
__LITSSH_SUDO_USERS__
###USER:app
0 6 * * * /home/app/job.sh
__LITSSH_SUDO_END__
""";

    [Fact]
    public async Task Refresh_loads_tasks_scripts_and_ai_prompt()
    {
        var script = "#!/bin/bash\necho hi\n";
        var ssh = new FakeSshService
        {
            OutputResolver = command =>
                command.Contains("__LITSSH_META__", StringComparison.Ordinal) ? DirectProbe :
                command.Contains("stat -c", StringComparison.Ordinal) ? "1234|755|root|root|1700000000" :
                command.Contains("base64", StringComparison.Ordinal) ? Convert.ToBase64String(Encoding.UTF8.GetBytes(script)) :
                string.Empty,
            SudoOutputResolver = _ => SudoProbe
        };
        var server = new SshServerConfig
        {
            Name = "web01",
            Host = "127.0.0.1",
            SudoType = SudoType.RootUser
        };
        await StaTest.RunAsync(async () =>
        {
            var sent = false;
            var viewModel = new ScheduledTasksViewModel(
                server,
                ssh,
                new FakeDialogService(),
                sendToAgent: (_, _) =>
                {
                    sent = true;
                    return Task.FromResult(true);
                });

            await viewModel.RefreshAsync();

            Assert.True(viewModel.Tasks.Count > 0, viewModel.StatusMessage);
            Assert.NotNull(viewModel.SelectedTask);
            Assert.Contains(viewModel.Tasks, t => t.ScheduleDescription.Contains("每天 03:00", StringComparison.Ordinal));
            Assert.Contains(viewModel.Tasks, t => t.IsPaused && t.Owner == "deploy");
            Assert.Contains(viewModel.Tasks, t => t.Kind == ScheduledTaskKind.SystemdTimer);

            var scriptTask = viewModel.Tasks.First(t => t.ScriptPath == "/opt/backup.sh");
            viewModel.SelectedTask = scriptTask;
            for (var i = 0; i < 50 && string.IsNullOrWhiteSpace(viewModel.ScriptMetaText); i++)
            {
                await Task.Delay(50);
            }

            Assert.Contains("权限 755", viewModel.ScriptMetaText);
            Assert.Contains("echo hi", viewModel.ScriptPreview);
            Assert.Contains("bash", viewModel.ScriptInterpreterText);

            var context = await viewModel.CreateScriptEditContextAsync();
            Assert.NotNull(context);
            Assert.Contains("echo hi", context!.Content);

            var prompt = await viewModel.BuildAiPromptAsync();
            Assert.NotNull(prompt);
            Assert.Contains("计划任务", prompt);
            Assert.Contains("/opt/backup.sh", prompt);
            Assert.Contains("echo hi", prompt);

            Assert.True(await viewModel.SendPromptToAgentAsync("prompt", true));
            Assert.True(sent);

            await viewModel.LoadElevatedAsync();
            Assert.Contains("已提权", viewModel.SudoText);
            Assert.Contains(viewModel.Tasks, t => t.Owner == "root");
            Assert.Contains(viewModel.Tasks, t => t.Owner == "app");
        });
    }

    [Fact]
    public async Task Pause_hint_and_labels_follow_task_kind()
    {
        var ssh = new FakeSshService
        {
            OutputResolver = command => command.Contains("__LITSSH_META__", StringComparison.Ordinal)
                ? DirectProbe
                : string.Empty
        };
        await StaTest.RunAsync(async () =>
        {
            var viewModel = new ScheduledTasksViewModel(
                new SshServerConfig { Name = "web01", Host = "127.0.0.1" },
                ssh,
                new FakeDialogService());

            await viewModel.RefreshAsync();

            viewModel.SelectedTask = viewModel.Tasks.First(t => t.IsPaused);
            Assert.Equal("启用任务", viewModel.PauseResumeText);
            Assert.Contains("#LITSSH_PAUSED#", viewModel.PauseResumeHint);

            viewModel.SelectedTask = viewModel.Tasks.First(t => t.Kind == ScheduledTaskKind.CronDirectory);
            Assert.Contains("目录任务", viewModel.PauseResumeHint);

            viewModel.SelectedTask = viewModel.Tasks.First(t => t.Kind == ScheduledTaskKind.SystemdTimer);
            Assert.Contains("systemd", viewModel.PauseResumeHint);
            Assert.Contains("暂停", viewModel.PauseResumeText);
        });
    }
}
