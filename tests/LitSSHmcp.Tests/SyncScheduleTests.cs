using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.Tests;

public class SyncScheduleTests
{
    [Fact]
    public void Interval_baseline_last_run_or_now()
    {
        var now = new DateTime(2026, 10, 8, 12, 0, 0);
        var t = new SyncTaskConfig { ScheduleType = SyncScheduleType.Interval, IntervalMinutes = 30 };
        Assert.Equal(now.AddMinutes(30), SyncSchedule.ComputeNextRun(t, now));

        t.LastRunAt = now.AddMinutes(-10);
        Assert.Equal(now.AddMinutes(20), SyncSchedule.ComputeNextRun(t, now));
    }

    [Fact]
    public void Weekly_picks_earliest_of_selected_days()
    {
        var now = new DateTime(2026, 10, 8, 12, 0, 0); // 2026-10-08 is Thursday
        var t = new SyncTaskConfig { ScheduleType = SyncScheduleType.Weekly, WeekDays = new[] { 1, 3 }, TimeOfDay = "03:00" };
        var next = SyncSchedule.ComputeNextRun(t, now);
        Assert.NotNull(next);
        Assert.True(next > now);
        Assert.Contains(next!.Value.DayOfWeek, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday });
        Assert.Equal(3, next.Value.Hour);
    }

    [Fact]
    public void Weekly_no_days_returns_null()
    {
        var now = new DateTime(2026, 10, 8, 12, 0, 0);
        var t = new SyncTaskConfig { ScheduleType = SyncScheduleType.Weekly, WeekDays = Array.Empty<int>() };
        Assert.Null(SyncSchedule.ComputeNextRun(t, now));
    }

    [Fact]
    public void Monthly_moves_to_next_month_when_past()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0);
        var t = new SyncTaskConfig { ScheduleType = SyncScheduleType.Monthly, MonthDays = new[] { 1 }, TimeOfDay = "03:00" };
        Assert.Equal(new DateTime(2026, 11, 1, 3, 0, 0), SyncSchedule.ComputeNextRun(t, now));
    }

    [Theory]
    [InlineData("", 3)]
    [InlineData("08:30", 8)]
    [InlineData("23:59", 23)]
    public void Parse_time_of_day(string input, int expectedHour)
        => Assert.Equal(expectedHour, SyncSchedule.ParseTimeOfDay(input).Hours);
}
