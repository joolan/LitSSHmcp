namespace LitSSHmcp.Core.Models;

/// <summary>同步方向（仅单向）。</summary>
public enum SyncDirection
{
    /// <summary>本地 → 远程服务器。</summary>
    LocalToRemote,

    /// <summary>远程服务器 → 本地。</summary>
    RemoteToLocal,

    /// <summary>远程服务器 → 远程服务器。</summary>
    RemoteToRemote
}

/// <summary>调度类型：固定间隔 / 每周固定时刻 / 每月固定时刻。</summary>
public enum SyncScheduleType
{
    Interval,
    Weekly,
    Monthly
}

/// <summary>同步任务 → 触发事件的联动：同步结束后按条件触发指定「触发事件」。</summary>
public class SyncEventHook
{
    /// <summary>目标触发事件 Id（须为「允许事件触发」的触发事件）。</summary>
    public string TriggerId { get; set; } = string.Empty;

    /// <summary>仅同步成功时触发（false = 无条件触发，即使同步失败也触发）。</summary>
    public bool OnlyOnSuccess { get; set; } = true;
}

/// <summary>一个文件夹同步任务（仅单向，运行于 App 进程内：关闭 App 即停止）。</summary>
public class SyncTaskConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public SyncDirection Direction { get; set; } = SyncDirection.LocalToRemote;

    /// <summary>本地路径（本机 ↔ 远程方向使用）。</summary>
    public string? LocalPath { get; set; }

    /// <summary>源服务器 Id（远程 → 本地 / 远程 → 远程）。</summary>
    public string? SourceServerId { get; set; }

    /// <summary>源远程路径（远程 → 本地 / 远程 → 远程）。</summary>
    public string? SourceRemotePath { get; set; }

    /// <summary>目标服务器 Id（本地 → 远程 / 远程 → 远程）。</summary>
    public string? TargetServerId { get; set; }

    /// <summary>目标远程路径（本地 → 远程 / 远程 → 远程）。</summary>
    public string? TargetRemotePath { get; set; }

    /// <summary>镜像删除目标端多余文件（默认 false=只新增/更新，不删除）。</summary>
    public bool DeleteExtra { get; set; }

    /// <summary>远程↔远程：源机连接目标时使用目标的内网地址（需目标配置 InternalHost；否则回退主机地址）。</summary>
    public bool UseInternalAddress { get; set; }

    /// <summary>仅同步匹配的文件（glob，如 <c>*.conf</c>、<c>logs/*.log</c>）；为空=全部。</summary>
    public string[] IncludePatterns { get; set; } = Array.Empty<string>();

    /// <summary>排除匹配的文件（glob）；为空=不排除。</summary>
    public string[] ExcludePatterns { get; set; } = Array.Empty<string>();

    public SyncScheduleType ScheduleType { get; set; } = SyncScheduleType.Interval;

    /// <summary>固定间隔（分钟，ScheduleType=Interval）。</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>每周几（多选）：0=周日 .. 6=周六（ScheduleType=Weekly）。</summary>
    public int[] WeekDays { get; set; } = new[] { 1 };

    /// <summary>每月几号（多选）：1..28（为避免无该日，最大 28）（ScheduleType=Monthly）。</summary>
    public int[] MonthDays { get; set; } = new[] { 1 };

    /// <summary>执行时刻 HH:mm（ScheduleType=Weekly/Monthly）。</summary>
    public string TimeOfDay { get; set; } = "03:00";

    /// <summary>失败重试次数 0..5（指数退避）。</summary>
    public int MaxRetries { get; set; } = 3;

    public DateTime? LastRunAt { get; set; }

    /// <summary>上次结果摘要：ok / fail:... </summary>
    public string? LastResult { get; set; }

    /// <summary>同步结束后要触发的「触发事件」及其条件。</summary>
    public SyncEventHook[] EventHooks { get; set; } = Array.Empty<SyncEventHook>();
}

/// <summary>同步调度计算（纯逻辑，便于单测）。</summary>
public static class SyncSchedule
{
    public static TimeSpan ParseTimeOfDay(string? value)
    {
        if (TimeSpan.TryParse(value, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
            return t;
        return new TimeSpan(3, 0, 0);
    }

    /// <summary>计算下一次运行时刻（相对 <paramref name="now"/>）；返回 null 表示未配置（不调度）。</summary>
    public static DateTime? ComputeNextRun(SyncTaskConfig task, DateTime now)
        => ComputeNextRun(task.ScheduleType, task.IntervalMinutes, task.WeekDays, task.MonthDays,
                          task.TimeOfDay, task.LastRunAt, now);

    /// <summary>参数化版本（触发事件等其它调度对象复用；字段语义与 <see cref="SyncTaskConfig"/> 一致）。</summary>
    public static DateTime? ComputeNextRun(SyncScheduleType type, int intervalMinutes, int[]? weekDays,
                                           int[]? monthDays, string? timeOfDay, DateTime? lastRunAt, DateTime now)
    {
        switch (type)
        {
            case SyncScheduleType.Weekly:
            {
                var time = ParseTimeOfDay(timeOfDay);
                var days = weekDays is { Length: > 0 } ? weekDays : Array.Empty<int>();
                DateTime? best = null;
                foreach (var d in days.Distinct())
                {
                    var target = (DayOfWeek)Math.Clamp(d, 0, 6);
                    var candidate = now.Date.Add(time);
                    candidate = candidate.AddDays(((int)target - (int)candidate.DayOfWeek + 7) % 7);
                    if (candidate <= now)
                        candidate = candidate.AddDays(7);
                    if (best is null || candidate < best)
                        best = candidate;
                }
                return best;
            }

            case SyncScheduleType.Monthly:
            {
                var time = ParseTimeOfDay(timeOfDay);
                var days = monthDays is { Length: > 0 } ? monthDays : Array.Empty<int>();
                DateTime? best = null;
                foreach (var dd in days.Distinct())
                {
                    var day = Math.Clamp(dd, 1, 28);
                    var candidate = new DateTime(now.Year, now.Month, Math.Min(day, DateTime.DaysInMonth(now.Year, now.Month))).Add(time);
                    if (candidate <= now)
                    {
                        var next = now.AddMonths(1);
                        candidate = new DateTime(next.Year, next.Month, Math.Min(day, DateTime.DaysInMonth(next.Year, next.Month))).Add(time);
                    }
                    if (best is null || candidate < best)
                        best = candidate;
                }
                return best;
            }

            default:
            {
                var interval = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
                var baseline = lastRunAt ?? now;
                var next = baseline + interval;
                while (next <= now)
                    next += interval;
                return next;
            }
        }
    }
}
