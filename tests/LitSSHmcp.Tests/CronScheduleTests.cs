using LitSSHmcp.Core.Services.Scheduling;
using Xunit;

namespace LitSSHmcp.Tests;

public class CronScheduleTests
{
    [Theory]
    [InlineData("*/5 * * * *", "每 5 分钟")]
    [InlineData("*/15 * * * *", "每 15 分钟")]
    [InlineData("0 * * * *", "每小时整点")]
    [InlineData("0 3 * * *", "每天 03:00")]
    [InlineData("30 2 * * 1-5", "工作日 02:30")]
    [InlineData("0 0 1 * *", "每月 1 日 00:00")]
    [InlineData("0 0 1 1 *", "每年 1 月的 1 日 00:00")]
    [InlineData("0 9 * * MON", "周一 09:00")]
    [InlineData("0 0 * * 0", "周日 00:00")]
    [InlineData("0 0 * * 1,3,5", "每周一、三、五 00:00")]
    [InlineData("0 0 * * 6,0", "周末 00:00")]
    [InlineData("0 0 1 * 1", "每月 1 日或周一 00:00（cron 规则：日或星期满足其一）")]
    [InlineData("@reboot", "系统启动时")]
    [InlineData("@daily", "每天 00:00")]
    [InlineData("@hourly", "每小时整点")]
    [InlineData("5-59/10 * * * *", "每小时的第 05、15、25、35、45、55 分钟")]
    public void Describes_common_expressions(string expression, string expected)
    {
        Assert.True(CronSchedule.TryParse(expression, out var schedule, out var error), error);
        Assert.NotNull(schedule);
        Assert.Equal(expected, schedule!.Describe());
    }

    [Fact]
    public void Rejects_invalid_expression()
    {
        Assert.False(CronSchedule.TryParse("61 * * * *", out _, out var error));
        Assert.Contains("取值无效", error);
    }

    [Fact]
    public void Computes_next_every_fifteen_minutes()
    {
        Assert.True(CronSchedule.TryParse("*/15 * * * *", out var schedule, out _));
        var next = schedule!.GetNextOccurrences(new DateTime(2026, 1, 1, 10, 7, 0), 3);
        Assert.Equal(new[]
        {
            new DateTime(2026, 1, 1, 10, 15, 0),
            new DateTime(2026, 1, 1, 10, 30, 0),
            new DateTime(2026, 1, 1, 10, 45, 0)
        }, next);
    }

    [Fact]
    public void Computes_next_daily_after_time_passed()
    {
        Assert.True(CronSchedule.TryParse("0 3 * * *", out var schedule, out _));
        var next = schedule!.GetNextOccurrences(new DateTime(2026, 1, 1, 4, 0, 0), 1);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 0, 0), Assert.Single(next));
    }

    [Fact]
    public void Computes_next_monthly()
    {
        Assert.True(CronSchedule.TryParse("0 0 1 * *", out var schedule, out _));
        var next = schedule!.GetNextOccurrences(new DateTime(2026, 1, 15, 0, 0, 0), 1);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0), Assert.Single(next));
    }

    [Fact]
    public void Computes_next_weekday()
    {
        Assert.True(CronSchedule.TryParse("0 0 * * 1", out var schedule, out _));
        var next = schedule!.GetNextOccurrences(new DateTime(2026, 1, 1, 0, 0, 0), 1);
        Assert.Equal(new DateTime(2026, 1, 5, 0, 0, 0), Assert.Single(next));
    }

    [Fact]
    public void Day_of_month_and_weekday_use_or_semantics()
    {
        Assert.True(CronSchedule.TryParse("0 0 1 * 1", out var schedule, out _));
        var next = schedule!.GetNextOccurrences(new DateTime(2026, 1, 2, 0, 0, 0), 1);
        Assert.Equal(new DateTime(2026, 1, 5, 0, 0, 0), Assert.Single(next));
    }

    [Fact]
    public void Reboot_has_no_next_occurrence()
    {
        Assert.True(CronSchedule.TryParse("@reboot", out var schedule, out _));
        Assert.Empty(schedule!.GetNextOccurrences(DateTime.Now, 3));
    }
}
