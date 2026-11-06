namespace LitSSHmcp.Core.Models;

public enum BackupScheduleType
{
    Interval,
    Daily,
    Weekly,
    Monthly
}

public class BackupOssConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Prefix { get; set; } = "litssh-backup";
    public string AccessKeyId { get; set; } = string.Empty;
    public string AccessKeySecretProtected { get; set; } = string.Empty;
    public bool UseHttps { get; set; } = true;
}

public class BackupPlanConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public BackupScheduleType ScheduleType { get; set; } = BackupScheduleType.Daily;
    public int IntervalMinutes { get; set; } = 1440;
    public int[] WeekDays { get; set; } = new[] { 1 };
    public int[] MonthDays { get; set; } = new[] { 1 };
    public string TimeOfDay { get; set; } = "03:00";

    public string[] Items { get; set; } = Array.Empty<string>();
    public string TargetDirectory { get; set; } = string.Empty;
    public bool UploadToOss { get; set; }
    public string OssPrefixOverride { get; set; } = string.Empty;
    public int KeepLocalCopies { get; set; } = 7;
    public int MaxRetries { get; set; } = 1;
    public bool EncryptBackup { get; set; }
    public string BackupPasswordProtected { get; set; } = string.Empty;
    public bool FallbackOnStartup { get; set; }

    public BackupPlanConfig Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        ScheduleType = ScheduleType,
        IntervalMinutes = IntervalMinutes,
        WeekDays = WeekDays.ToArray(),
        MonthDays = MonthDays.ToArray(),
        TimeOfDay = TimeOfDay,
        Items = Items.ToArray(),
        TargetDirectory = TargetDirectory,
        UploadToOss = UploadToOss,
        OssPrefixOverride = OssPrefixOverride,
        KeepLocalCopies = KeepLocalCopies,
        MaxRetries = MaxRetries,
        EncryptBackup = EncryptBackup,
        BackupPasswordProtected = BackupPasswordProtected,
        FallbackOnStartup = FallbackOnStartup
    };
}

public class BackupConfig
{
    public BackupPlanConfig[] Plans { get; set; } = Array.Empty<BackupPlanConfig>();
    public BackupOssConfig Oss { get; set; } = new();
}

public static class BackupSchedule
{
    public static DateTime? ComputeNextRun(BackupPlanConfig plan, DateTime now, DateTime? lastRunAt)
    {
        var type = plan.ScheduleType switch
        {
            BackupScheduleType.Daily => SyncScheduleType.Weekly,
            BackupScheduleType.Weekly => SyncScheduleType.Weekly,
            BackupScheduleType.Monthly => SyncScheduleType.Monthly,
            _ => SyncScheduleType.Interval
        };
        var weekDays = plan.ScheduleType == BackupScheduleType.Daily
            ? new[] { 0, 1, 2, 3, 4, 5, 6 }
            : plan.WeekDays;
        if (plan.ScheduleType == BackupScheduleType.Daily)
        {
            var time = SyncSchedule.ParseTimeOfDay(plan.TimeOfDay);
            var candidate = now.Date.Add(time);
            if (candidate <= now)
                candidate = candidate.AddDays(1);
            return candidate;
        }
        return SyncSchedule.ComputeNextRun(type, plan.IntervalMinutes, weekDays, plan.MonthDays,
            plan.TimeOfDay, lastRunAt, now);
    }
}
