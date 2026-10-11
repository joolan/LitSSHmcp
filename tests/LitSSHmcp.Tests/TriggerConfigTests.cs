using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.Tests;

public class TriggerConfigTests
{
    private static TriggerConfig ValidConfig() => new()
    {
        Name = "构建发布",
        Actions = new[]
        {
            new TriggerAction { Type = TriggerActionType.GitCommitPush, GitDir = "/srv/app" },
            new TriggerAction { Type = TriggerActionType.Webhook, Url = "https://example.com/hook" },
            new TriggerAction { Type = TriggerActionType.Backup, SourcePath = "/srv/app", DestDir = "/backup" }
        }
    };

    [Fact]
    public void Valid_config_has_no_errors()
        => Assert.Empty(ValidConfig().Validate());

    [Fact]
    public void Name_is_required()
    {
        var c = ValidConfig();
        c.Name = "  ";
        Assert.Contains(c.Validate(), e => e.Contains("名称"));
    }

    [Fact]
    public void Remote_target_requires_server()
    {
        var c = ValidConfig();
        c.TargetType = TriggerTarget.Remote;
        c.ServerId = null;
        Assert.Contains(c.Validate(), e => e.Contains("服务器"));
    }

    [Fact]
    public void At_least_one_action_required()
    {
        var c = ValidConfig();
        c.Actions = Array.Empty<TriggerAction>();
        Assert.Contains(c.Validate(), e => e.Contains("动作"));
    }

    [Fact]
    public void Action_fields_validated_per_type()
    {
        var c = ValidConfig();
        c.Actions = new[]
        {
            new TriggerAction { Type = TriggerActionType.GitCommitPush },
            new TriggerAction { Type = TriggerActionType.Webhook, Url = "ftp://bad" },
            new TriggerAction { Type = TriggerActionType.Backup, SourcePath = "/src" }
        };
        var errors = c.Validate();
        Assert.Contains(errors, e => e.Contains("git") && e.Contains("仓库目录"));
        Assert.Contains(errors, e => e.Contains("webhook") && e.Contains("http"));
        Assert.Contains(errors, e => e.Contains("备份") && e.Contains("输出目录"));
    }

    [Fact]
    public void Schedule_validation_applies_only_when_enabled()
    {
        var c = ValidConfig();
        c.ScheduleEnabled = false;
        c.IntervalMinutes = 0;
        c.ScheduleType = SyncScheduleType.Weekly;
        c.WeekDays = Array.Empty<int>();
        Assert.Empty(c.Validate());

        c.ScheduleEnabled = true;
        Assert.Contains(c.Validate(), e => e.Contains("每周"));

        c.ScheduleType = SyncScheduleType.Interval;
        Assert.Contains(c.Validate(), e => e.Contains("间隔"));
    }

    [Fact]
    public void Parameterized_next_run_matches_task_overload()
    {
        var now = new DateTime(2026, 10, 8, 12, 0, 0);

        foreach (var (type, lastRun) in new[]
        {
            (SyncScheduleType.Interval, (DateTime?)null),
            (SyncScheduleType.Interval, (DateTime?)now.AddMinutes(-10)),
            (SyncScheduleType.Weekly, (DateTime?)now.AddDays(-2)),
            (SyncScheduleType.Monthly, (DateTime?)null)
        })
        {
            var task = new SyncTaskConfig
            {
                ScheduleType = type,
                IntervalMinutes = 30,
                WeekDays = new[] { 1, 3 },
                MonthDays = new[] { 5, 20 },
                TimeOfDay = "03:00",
                LastRunAt = lastRun
            };

            var fromTask = SyncSchedule.ComputeNextRun(task, now);
            var fromParams = SyncSchedule.ComputeNextRun(task.ScheduleType, task.IntervalMinutes,
                task.WeekDays, task.MonthDays, task.TimeOfDay, task.LastRunAt, now);
            Assert.Equal(fromTask, fromParams);
        }
    }

    [Fact]
    public void Trigger_schedule_uses_parameterized_overload()
    {
        var now = new DateTime(2026, 10, 8, 12, 0, 0);
        var trigger = new TriggerConfig { ScheduleType = SyncScheduleType.Interval, IntervalMinutes = 45 };
        var next = SyncSchedule.ComputeNextRun(trigger.ScheduleType, trigger.IntervalMinutes,
            trigger.WeekDays, trigger.MonthDays, trigger.TimeOfDay, trigger.LastRunAt, now);
        Assert.Equal(now.AddMinutes(45), next);
    }

    [Fact]
    public void Event_trigger_allowed_by_default_and_sync_hooks_default_empty()
    {
        Assert.True(new TriggerConfig().AllowEventTrigger);

        var task = new SyncTaskConfig();
        Assert.Empty(task.EventHooks);

        var hook = new SyncEventHook { TriggerId = "t1" };
        Assert.True(hook.OnlyOnSuccess); // 默认仅成功时触发
    }

    [Fact]
    public void Path_fields_raise_change_notification()
    {
        var action = new TriggerAction();
        var raised = new List<string>();
        action.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        action.GitDir = "/srv/app";
        action.SourcePath = "/data";
        action.DestDir = "/backup";

        Assert.Contains(nameof(TriggerAction.GitDir), raised);
        Assert.Contains(nameof(TriggerAction.SourcePath), raised);
        Assert.Contains(nameof(TriggerAction.DestDir), raised);
    }
}

public class TriggerTemplateTests
{
    [Fact]
    public void Renders_known_keys_case_insensitively()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["trigger"] = "构建发布",
            ["result"] = "成功"
        };
        Assert.Equal("构建发布: 成功", TriggerTemplate.Render("{{trigger}}: {{Result}}", vars));
    }

    [Fact]
    public void Unknown_keys_and_json_braces_are_preserved()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["trigger"] = "T" };
        var input = "{\"text\":\"{{trigger}}\",\"other\":1,\"nested\":{\"trigger\":2}}";
        // 未被 {{...}} 包裹的结构性花括号与字面键名不受影响
        var output = TriggerTemplate.Render(input, vars);
        Assert.Equal("{\"text\":\"T\",\"other\":1,\"nested\":{\"trigger\":2}}", output);
    }

    [Fact]
    public void Empty_template_renders_empty()
        => Assert.Equal(string.Empty, TriggerTemplate.Render(null, new Dictionary<string, string>()));
}
