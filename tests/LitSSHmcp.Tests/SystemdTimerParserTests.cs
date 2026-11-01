using LitSSHmcp.Core.Services.Scheduling;
using Xunit;

namespace LitSSHmcp.Tests;

public class SystemdTimerParserTests
{
    private const string TimerProperties = """
Id=backup.timer
Description=Daily backup timer
Unit=backup.service
ActiveState=active
SubState=waiting
UnitFileState=enabled
NextElapseUSecRealtime=0
LastTriggerUSec=0
TimersCalendar={ OnCalendar=*-*-* 03:00:00 ; next_elapse=0 }
TimersMonotonic={ OnUnitActiveSec=1h ; last_trigger=0 }
FragmentPath=/etc/systemd/system/backup.timer
""";

    private const string ServiceProperties = """
Id=backup.service
Description=Daily backup service
User=root
WorkingDirectory=/opt
ExecStart={ path=/bin/bash ; argv[]=/bin/bash /opt/backup.sh ; ignore_errors=no ; start_time=[n/a] ; stop_time=[n/a] ; pid=0 ; code=(null) ; status=0/0 }
FragmentPath=/etc/systemd/system/backup.service
""";

    [Fact]
    public void Parses_timer_and_service()
    {
        var timer = SystemdTimerParser.Parse("backup.timer", TimerProperties, ServiceProperties);

        Assert.Equal("backup.timer", timer.Unit);
        Assert.Equal("backup.service", timer.ActivatedUnit);
        Assert.True(timer.IsActive);
        Assert.Equal("运行中", timer.StateText);
        Assert.Equal("/bin/bash /opt/backup.sh", timer.ServiceCommand);
        Assert.Equal("/etc/systemd/system/backup.timer", timer.FragmentPath);
        Assert.Contains("每天 03:00", timer.ScheduleDescription);
        Assert.Contains("距上次运行 1 小时", timer.ScheduleDescription);
    }

    [Theory]
    [InlineData("*-*-* 03:00:00", "每天 03:00")]
    [InlineData("Mon..Fri *-*-* 09:00", "工作日 09:00")]
    [InlineData("Mon,Wed,Fri *-*-* 09:00", "每周一、三、五 09:00")]
    [InlineData("*-*-01 04:00", "每月 1 日 04:00")]
    [InlineData("*-01-01 00:00", "每年 1 月 1 日 00:00")]
    [InlineData("*:0/15", "每 15 分钟")]
    [InlineData("*-*-* *:0/15:00", "每 15 分钟")]
    [InlineData("daily", "每天 00:00")]
    public void Describes_calendar_expressions(string expression, string expected)
    {
        Assert.Equal(expected, SystemdTimerParser.DescribeCalendar(expression));
    }

    [Fact]
    public void Parses_weekday_range_with_date_and_time()
    {
        Assert.Equal("工作日 09:30", SystemdTimerParser.DescribeCalendar("Mon..Fri *-*-* 09:30:00"));
    }
}
