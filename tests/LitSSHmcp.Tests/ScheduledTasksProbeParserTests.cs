using LitSSHmcp.Core.Services.Scheduling;
using Xunit;

namespace LitSSHmcp.Tests;

public class ScheduledTasksProbeParserTests
{
    [Fact]
    public void Builds_tasks_from_probe_output()
    {
        const string output = """
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
MAILTO=ops@example.com
0 3 * * * /usr/local/bin/backup.sh
__LITSSH_ETC_CRONTAB__
SHELL=/bin/sh
17 * * * * root cd / && run-parts --report /etc/cron.hourly
__LITSSH_CRON_D__
###FILE:/etc/cron.d/app
*/5 * * * * app /opt/app/check.sh
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

        const string sudoOutput = """
__LITSSH_SUDO_META__
root
__LITSSH_SUDO_CRONTAB__
0 4 * * * /root/nightly.sh
__LITSSH_SUDO_USERS__
###USER:app
0 6 * * * /home/app/job.sh
__LITSSH_SUDO_END__
""";

        var data = ScheduledTasksProbeParser.Parse(output, sudoOutput);
        var tasks = data.BuildTasks();

        Assert.Equal("web01", data.HostName);
        Assert.Equal(TimeSpan.FromHours(8), data.ServerNow.Offset);
        Assert.Equal("root", data.ElevatedUser);
        Assert.Contains(tasks, t => t.Kind == ScheduledTaskKind.UserCrontab && t.Owner == "deploy" &&
                                     t.ScheduleDescription == "每天 03:00");
        Assert.Contains(tasks, t => t.Kind == ScheduledTaskKind.SystemCrontab && t.Owner == "root");
        Assert.Contains(tasks, t => t.Kind == ScheduledTaskKind.CronD && t.Owner == "app");
        Assert.Contains(tasks, t => t.Kind == ScheduledTaskKind.CronDirectory && t.IsPaused == false);
        Assert.Contains(tasks, t => t.Kind == ScheduledTaskKind.UserCrontab && t.Owner == "root");
        Assert.Contains(tasks, t => t.Kind == ScheduledTaskKind.UserCrontab && t.Owner == "app");
        Assert.Contains(tasks, t => t.Kind == ScheduledTaskKind.SystemdTimer &&
                                     t.ScriptPath == "/opt/backup.sh");
    }

    [Fact]
    public void Marks_non_executable_cron_directory_as_paused()
    {
        const string output = """
__LITSSH_META__
user=deploy
date=2026-01-01T10:00:00+0800
__LITSSH_CURRENT_CRONTAB__
no crontab for deploy
__LITSSH_ETC_CRONTAB__
__LITSSH_CRON_D__
__LITSSH_CRON_DIRS__
###FILE:/etc/cron.daily/app|644|root|root|1234|1700000000
__LITSSH_TIMERS__
__LITSSH_END__
""";

        var data = ScheduledTasksProbeParser.Parse(output, null);
        var task = Assert.Single(data.BuildTasks());

        Assert.Equal(ScheduledTaskKind.CronDirectory, task.Kind);
        Assert.True(task.IsPaused);
        Assert.Contains("无执行权限", task.StateText);
    }
}
