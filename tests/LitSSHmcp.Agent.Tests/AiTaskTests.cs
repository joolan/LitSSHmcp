using LitSSHmcp.Agent.Scheduling;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class AiTaskScheduleTests
{
    private static AiTaskConfig Daily(string time = "08:00") => new()
    {
        Name = "每日巡检",
        Instruction = "巡检磁盘",
        ScheduleType = AiTaskScheduleType.Daily,
        TimeOfDay = time
    };

    [Fact]
    public void Daily_before_time_returns_today()
    {
        var now = new DateTime(2026, 11, 8, 7, 0, 0);
        var next = AiTaskSchedule.ComputeNextRun(Daily("08:00"), now);
        Assert.Equal(new DateTime(2026, 11, 8, 8, 0, 0), next);
    }

    [Fact]
    public void Daily_after_time_returns_tomorrow()
    {
        var now = new DateTime(2026, 11, 8, 9, 0, 0);
        var next = AiTaskSchedule.ComputeNextRun(Daily("08:00"), now);
        Assert.Equal(new DateTime(2026, 11, 9, 8, 0, 0), next);
    }

    [Fact]
    public void Weekly_picks_next_selected_day()
    {
        var task = new AiTaskConfig
        {
            Name = "周报",
            Instruction = "汇总",
            ScheduleType = AiTaskScheduleType.Weekly,
            TimeOfDay = "09:30",
            WeekDays = new[] { 1, 5 }   // 周一/周五
        };
        // 2026-11-08 是周日
        var next = AiTaskSchedule.ComputeNextRun(task, new DateTime(2026, 11, 8, 10, 0, 0));
        Assert.Equal(new DateTime(2026, 11, 9, 9, 30, 0), next);
    }

    [Fact]
    public void Monthly_picks_next_date()
    {
        var task = new AiTaskConfig
        {
            Name = "月报",
            Instruction = "汇总",
            ScheduleType = AiTaskScheduleType.Monthly,
            TimeOfDay = "09:00",
            MonthDays = new[] { 1, 15 }
        };
        var next = AiTaskSchedule.ComputeNextRun(task, new DateTime(2026, 11, 8, 10, 0, 0));
        Assert.Equal(new DateTime(2026, 11, 15, 9, 0, 0), next);
    }

    [Fact]
    public void Once_in_future_returns_time_and_past_returns_null()
    {
        var task = new AiTaskConfig
        {
            Name = "一次性",
            Instruction = "执行",
            ScheduleType = AiTaskScheduleType.Once,
            OnceAt = new DateTime(2026, 11, 9, 8, 0, 0)
        };
        Assert.Equal(new DateTime(2026, 11, 9, 8, 0, 0),
            AiTaskSchedule.ComputeNextRun(task, new DateTime(2026, 11, 8, 10, 0, 0)));

        task.OnceAt = new DateTime(2026, 11, 7, 8, 0, 0);
        Assert.Null(AiTaskSchedule.ComputeNextRun(task, new DateTime(2026, 11, 8, 10, 0, 0)));
    }

    [Fact]
    public void Interval_advances_past_now()
    {
        var task = new AiTaskConfig
        {
            Name = "间隔",
            Instruction = "执行",
            ScheduleType = AiTaskScheduleType.Interval,
            IntervalMinutes = 30
        };
        var next = AiTaskSchedule.ComputeNextRun(task, new DateTime(2026, 11, 8, 10, 0, 0));
        Assert.Equal(new DateTime(2026, 11, 8, 10, 30, 0), next);
    }

    [Fact]
    public void Describe_contains_schedule_summary()
    {
        Assert.Contains("每天 08:00", AiTaskSchedule.Describe(Daily()));
        Assert.Contains("每 30 分钟", AiTaskSchedule.Describe(new AiTaskConfig
        {
            ScheduleType = AiTaskScheduleType.Interval,
            IntervalMinutes = 30
        }));
    }
}

public class AiTaskTemplateTests
{
    [Fact]
    public void Templates_are_valid_and_read_only()
    {
        Assert.NotEmpty(AiTaskTemplates.All);
        foreach (var template in AiTaskTemplates.All)
        {
            var task = AiTaskTemplates.CreateTask(template);
            Assert.True(task.ReadOnly);
            Assert.Equal("Template", task.Source);
            Assert.Contains(template.Name, task.Name);
            Assert.Empty(task.Validate());
        }
    }
}

public class AiTaskStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
        "litssh-aitask-" + Guid.NewGuid().ToString("N") + ".db");

    private AiTaskStore NewStore() => new(_dbPath);

    [Fact]
    public async Task Task_crud_and_run_history_roundtrip()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var task = new AiTaskConfig
        {
            Name = "每日磁盘巡检",
            Instruction = "巡检所有服务器磁盘并给出结论",
            ScheduleType = AiTaskScheduleType.Daily,
            TimeOfDay = "08:00",
            OwnerUserId = "local"
        };
        await store.CreateAsync(task);

        var list = await store.ListAsync();
        Assert.Single(list);
        Assert.Equal("每日磁盘巡检", list[0].Name);
        Assert.NotNull(list[0].NextRunAt);

        var loaded = await store.GetAsync(task.Id);
        Assert.NotNull(loaded);
        Assert.Equal(task.Instruction, loaded!.Instruction);

        await store.SetEnabledAsync(task.Id, false);
        Assert.False((await store.GetAsync(task.Id))!.Enabled);

        var runId = await store.BeginRunAsync(task.Id, AiTaskTrigger.Manual);
        await store.CompleteRunAsync(runId, AiTaskRunStatus.Succeeded, 42,
            "磁盘正常", null, 120, 80);

        var runs = await store.ListRunsAsync(task.Id);
        Assert.Single(runs);
        Assert.Equal(AiTaskRunStatus.Succeeded, runs[0].Status);
        Assert.Equal(42, runs[0].SessionId);
        Assert.Equal(200, runs[0].InputTokens + runs[0].OutputTokens);

        await store.UpdateAfterRunAsync(task.Id, DateTime.Now.AddDays(1), "ok", 42, false);
        var after = await store.GetAsync(task.Id);
        Assert.Equal("ok", after!.LastResult);
        Assert.Equal(42, after.LastSessionId);

        await store.DeleteAsync(task.Id);
        Assert.Empty(await store.ListAsync());
        Assert.Empty(await store.ListRunsAsync(task.Id));
    }

    [Fact]
    public async Task Lease_is_exclusive_until_released()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var task = new AiTaskConfig
        {
            Name = "租约测试",
            Instruction = "执行",
            ScheduleType = AiTaskScheduleType.Daily,
            TimeOfDay = "08:00"
        };
        await store.CreateAsync(task);

        Assert.True(await store.TryAcquireLeaseAsync(task.Id, "app-1", TimeSpan.FromMinutes(5)));
        Assert.False(await store.TryAcquireLeaseAsync(task.Id, "app-2", TimeSpan.FromMinutes(5)));

        await store.ReleaseLeaseAsync(task.Id, "app-1");
        Assert.True(await store.TryAcquireLeaseAsync(task.Id, "app-2", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Run_requested_is_recorded_for_scheduler()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var task = new AiTaskConfig
        {
            Name = "立即运行",
            Instruction = "执行",
            ScheduleType = AiTaskScheduleType.Daily,
            TimeOfDay = "08:00"
        };
        await store.CreateAsync(task);

        Assert.Null((await store.GetAsync(task.Id))!.RunRequestedAt);
        await store.RequestRunAsync(task.Id);
        Assert.NotNull((await store.GetAsync(task.Id))!.RunRequestedAt);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
        }
    }
}
