using System.Globalization;

namespace LitSSHmcp.Agent.Scheduling;

/// <summary>AI 定时任务的调度类型。</summary>
public enum AiTaskScheduleType
{
    /// <summary>一次性，到 <see cref="AiTaskConfig.OnceAt"/> 执行一次后停用。</summary>
    Once,
    /// <summary>固定间隔（分钟）。</summary>
    Interval,
    /// <summary>每天固定时刻。</summary>
    Daily,
    /// <summary>每周多选周几。</summary>
    Weekly,
    /// <summary>每月多选日期（1-28）。</summary>
    Monthly
}

/// <summary>本次运行的触发来源。</summary>
public enum AiTaskTrigger
{
    Schedule,
    Manual,
    CatchUp,
    Headless
}

/// <summary>运行状态。</summary>
public enum AiTaskRunStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Skipped
}

/// <summary>
/// 一条 AI 定时任务。任务指令是自然语言 prompt，到点后由 HeadlessAgentRunner
/// 创建新会话并发送该指令，由大模型自主调用工具执行。
/// </summary>
public sealed class AiTaskConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Instruction { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public AiTaskScheduleType ScheduleType { get; set; } = AiTaskScheduleType.Daily;

    /// <summary>固定间隔（分钟，ScheduleType=Interval）。</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>每周几（多选）：0=周日 .. 6=周六（ScheduleType=Weekly）。</summary>
    public int[] WeekDays { get; set; } = new[] { 1 };

    /// <summary>每月几号（多选）：1..28（ScheduleType=Monthly）。</summary>
    public int[] MonthDays { get; set; } = new[] { 1 };

    /// <summary>执行时刻 HH:mm（Daily/Weekly/Monthly）。</summary>
    public string TimeOfDay { get; set; } = "08:00";

    /// <summary>一次性执行时间（ScheduleType=Once）。</summary>
    public DateTime? OnceAt { get; set; }

    /// <summary>指定模型 Id；空=使用 `agent.activeProviderId` 或第一个可用模型。</summary>
    public string? ProviderId { get; set; }

    /// <summary>指定智能体 Id；空=不附加智能体提示。</summary>
    public string? AgentId { get; set; }

    /// <summary>只读模式（推荐默认 true）。只读模式只暴露只读工具，不触发审批。</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>工具调用最大轮数；0=使用 agent.maxToolIterations。</summary>
    public int MaxTurns { get; set; }

    /// <summary>单次运行墙钟超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 1800;

    /// <summary>单次运行估算 token 预算；0=不限制（仍受全局预算约束）。</summary>
    public int TokenBudget { get; set; }

    public bool NotifyApp { get; set; } = true;
    public bool NotifyOnlyOnIssue { get; set; } = true;

    /// <summary>邮件收件人分组 Id（与巡检文档共用；首期可留空）。</summary>
    public string[] EmailGroupIds { get; set; } = Array.Empty<string>();

    /// <summary>Webhook 目标 Id（首期可留空，后续接入）。</summary>
    public string[] WebhookIds { get; set; } = Array.Empty<string>();

    /// <summary>App 启动时若本周期已过且未成功，是否补跑一次。</summary>
    public bool RunOnStartupIfMissed { get; set; } = true;

    /// <summary>创建来源：User / AI。</summary>
    public string Source { get; set; } = "User";

    /// <summary>任务归属用户（桌面默认 local；Web 为用户 Id）。</summary>
    public string OwnerUserId { get; set; } = "local";

    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastResult { get; set; }
    public long? LastSessionId { get; set; }

    /// <summary>手动“立即运行”请求时间；调度器看到后执行一次。</summary>
    public DateTime? RunRequestedAt { get; set; }

    /// <summary>当前租约持有者（跨进程防重复执行）。</summary>
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntil { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>调度描述（列表/工具返回用）。</summary>
    public string DescribeSchedule() => AiTaskSchedule.Describe(this);

    /// <summary>校验配置，返回错误列表（空=通过）。</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("任务名称不能为空");
        if (string.IsNullOrWhiteSpace(Instruction))
            errors.Add("任务指令不能为空");

        switch (ScheduleType)
        {
            case AiTaskScheduleType.Once:
                if (OnceAt is null)
                    errors.Add("一次性任务必须指定执行时间");
                else if (OnceAt.Value <= DateTime.Now)
                    errors.Add("一次性任务的执行时间必须晚于当前时间");
                break;
            case AiTaskScheduleType.Interval:
                if (IntervalMinutes < 5)
                    errors.Add("固定间隔至少 5 分钟");
                break;
            case AiTaskScheduleType.Daily:
                if (!AiTaskSchedule.TryParseTimeOfDay(TimeOfDay, out _))
                    errors.Add("每天任务必须指定有效时刻 HH:mm");
                break;
            case AiTaskScheduleType.Weekly:
                if (WeekDays is null || WeekDays.Length == 0 || WeekDays.Any(d => d < 0 || d > 6))
                    errors.Add("每周任务需至少选择一个有效周几（0-6）");
                if (!AiTaskSchedule.TryParseTimeOfDay(TimeOfDay, out _))
                    errors.Add("每周任务必须指定有效时刻 HH:mm");
                break;
            case AiTaskScheduleType.Monthly:
                if (MonthDays is null || MonthDays.Length == 0 || MonthDays.Any(d => d < 1 || d > 28))
                    errors.Add("每月任务需至少选择一个有效日期（1-28）");
                if (!AiTaskSchedule.TryParseTimeOfDay(TimeOfDay, out _))
                    errors.Add("每月任务必须指定有效时刻 HH:mm");
                break;
        }

        if (TimeoutSeconds is < 30 or > 7200)
            errors.Add("超时时间需在 30-7200 秒之间");
        if (TokenBudget < 0)
            errors.Add("token 预算不能为负数");
        return errors;
    }
}

/// <summary>一次 AI 定时任务运行记录。</summary>
public sealed class AiTaskRun
{
    public long Id { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public long? SessionId { get; set; }
    public AiTaskTrigger Trigger { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public AiTaskRunStatus Status { get; set; } = AiTaskRunStatus.Running;
    public string? Error { get; set; }
    public string? Summary { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

/// <summary>AI 定时任务调度计算（纯逻辑，便于单测）。</summary>
public static class AiTaskSchedule
{
    public static bool TryParseTimeOfDay(string? value, out TimeSpan time)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time) &&
            time >= TimeSpan.Zero && time < TimeSpan.FromDays(1))
            return true;
        time = new TimeSpan(8, 0, 0);
        return false;
    }

    public static TimeSpan ParseTimeOfDay(string? value) =>
        TryParseTimeOfDay(value, out var time) ? time : new TimeSpan(8, 0, 0);

    /// <summary>
    /// 计算下一次运行时刻（相对 <paramref name="now"/>）；返回 null 表示不再调度
    /// （一次性任务已到点/已执行，或配置无效）。
    /// </summary>
    public static DateTime? ComputeNextRun(AiTaskConfig task, DateTime now)
    {
        switch (task.ScheduleType)
        {
            case AiTaskScheduleType.Once:
                if (task.OnceAt is null || task.OnceAt.Value <= now)
                    return null;
                return task.OnceAt.Value;

            case AiTaskScheduleType.Interval:
            {
                var interval = TimeSpan.FromMinutes(Math.Max(1, task.IntervalMinutes));
                var baseline = task.LastRunAt ?? now;
                var next = baseline + interval;
                while (next <= now)
                    next += interval;
                return next;
            }

            case AiTaskScheduleType.Daily:
            {
                var time = ParseTimeOfDay(task.TimeOfDay);
                var candidate = now.Date.Add(time);
                if (candidate <= now)
                    candidate = candidate.AddDays(1);
                return candidate;
            }

            case AiTaskScheduleType.Weekly:
            {
                var time = ParseTimeOfDay(task.TimeOfDay);
                var days = task.WeekDays is { Length: > 0 } ? task.WeekDays : Array.Empty<int>();
                DateTime? best = null;
                foreach (var day in days.Distinct())
                {
                    var target = (DayOfWeek)Math.Clamp(day, 0, 6);
                    var candidate = now.Date.Add(time);
                    candidate = candidate.AddDays(((int)target - (int)candidate.DayOfWeek + 7) % 7);
                    if (candidate <= now)
                        candidate = candidate.AddDays(7);
                    if (best is null || candidate < best)
                        best = candidate;
                }
                return best;
            }

            case AiTaskScheduleType.Monthly:
            {
                var time = ParseTimeOfDay(task.TimeOfDay);
                var days = task.MonthDays is { Length: > 0 } ? task.MonthDays : Array.Empty<int>();
                DateTime? best = null;
                foreach (var raw in days.Distinct())
                {
                    var day = Math.Clamp(raw, 1, 28);
                    var candidate = new DateTime(now.Year, now.Month,
                        Math.Min(day, DateTime.DaysInMonth(now.Year, now.Month))).Add(time);
                    if (candidate <= now)
                    {
                        var nextMonth = now.AddMonths(1);
                        candidate = new DateTime(nextMonth.Year, nextMonth.Month,
                            Math.Min(day, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month))).Add(time);
                    }
                    if (best is null || candidate < best)
                        best = candidate;
                }
                return best;
            }

            default:
                return null;
        }
    }

    /// <summary>调度描述（中文，供 UI 与 AI 工具返回）。</summary>
    public static string Describe(AiTaskConfig task)
    {
        static string WeekName(int day) => day switch
        {
            0 => "周日", 1 => "周一", 2 => "周二", 3 => "周三",
            4 => "周四", 5 => "周五", 6 => "周六", _ => $"周{day}"
        };

        return task.ScheduleType switch
        {
            AiTaskScheduleType.Once =>
                task.OnceAt is { } once ? $"一次性 {once:yyyy-MM-dd HH:mm}" : "一次性（未设置时间）",
            AiTaskScheduleType.Interval => $"每 {Math.Max(1, task.IntervalMinutes)} 分钟",
            AiTaskScheduleType.Daily => $"每天 {task.TimeOfDay}",
            AiTaskScheduleType.Weekly =>
                $"每周 {string.Join("、", (task.WeekDays ?? Array.Empty<int>()).Distinct().OrderBy(d => d).Select(WeekName))} {task.TimeOfDay}",
            AiTaskScheduleType.Monthly =>
                $"每月 {string.Join("、", (task.MonthDays ?? Array.Empty<int>()).Distinct().OrderBy(d => d).Select(d => $"{d} 日"))} {task.TimeOfDay}",
            _ => task.ScheduleType.ToString()
        };
    }
}
