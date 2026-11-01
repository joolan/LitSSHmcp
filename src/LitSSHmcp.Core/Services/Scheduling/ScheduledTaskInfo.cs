namespace LitSSHmcp.Core.Services.Scheduling;

public enum ScheduledTaskKind
{
    UserCrontab,
    SystemCrontab,
    CronD,
    CronDirectory,
    SystemdTimer
}

public sealed class ScheduledTaskInfo
{
    public string Id { get; init; } = string.Empty;
    public ScheduledTaskKind Kind { get; init; }
    public string Owner { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public string RawLine { get; init; } = string.Empty;
    public string ScheduleText { get; init; } = string.Empty;
    public string ScheduleDescription { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public bool IsPaused { get; init; }
    public string StateText { get; init; } = string.Empty;
    public string NextRunText { get; init; } = string.Empty;
    public string LastRunText { get; init; } = string.Empty;
    public string? ScriptPath { get; init; }
    public string? ScriptInterpreter { get; init; }
    public string? UnitName { get; init; }
    public string? ActivatedUnit { get; init; }
    public string? UnitFileState { get; init; }
    public string? ActiveState { get; init; }
    public string? FragmentPath { get; init; }
    public string Description { get; init; } = string.Empty;

    public string KindText => Kind switch
    {
        ScheduledTaskKind.UserCrontab => "用户 crontab",
        ScheduledTaskKind.SystemCrontab => "系统 crontab",
        ScheduledTaskKind.CronD => "cron.d",
        ScheduledTaskKind.CronDirectory => "目录任务",
        ScheduledTaskKind.SystemdTimer => "systemd 定时器",
        _ => "计划任务"
    };

    public string SourceDisplay => LineNumber > 0
        ? $"{SourcePath}:{LineNumber}"
        : SourcePath;

    public bool IsSystemd => Kind == ScheduledTaskKind.SystemdTimer;
    public bool IsCronFile => Kind is ScheduledTaskKind.UserCrontab
        or ScheduledTaskKind.SystemCrontab
        or ScheduledTaskKind.CronD;
}
