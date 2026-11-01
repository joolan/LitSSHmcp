using System.Globalization;

namespace LitSSHmcp.Core.Services.Scheduling;

public sealed class ScheduledTasksProbeData
{
    public Dictionary<string, string> Meta { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CurrentCrontab { get; set; }
    public string? EtcCrontab { get; set; }
    public List<KeyValuePair<string, string>> UserCrontabs { get; } = new();
    public List<KeyValuePair<string, string>> CronDFiles { get; } = new();
    public List<CronDirScriptInfo> CronDirScripts { get; } = new();
    public List<SystemdTimerInfo> Timers { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? ElevatedUser { get; set; }
    public string? ElevatedCrontab { get; set; }
    public bool SudoSucceeded { get; set; }

    public string CurrentUser => GetMeta("user");
    public string HostName => GetMeta("hostname");
    public string OsName => GetMeta("os");
    public string Kernel => GetMeta("kernel");
    public string CronState => GetMeta("cron_state");
    public string ServerTimeText => GetMeta("date");
    public string CrontabPath => GetMeta("crontab");
    public string SystemctlPath => GetMeta("systemctl");

    public DateTimeOffset ServerNow
    {
        get
        {
            var value = GetMeta("date");
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)
                ? parsed
                : DateTimeOffset.Now;
        }
    }

    public IReadOnlyList<ScheduledTaskInfo> BuildTasks()
    {
        var serverNow = ServerNow;
        var tasks = new List<ScheduledTaskInfo>();
        var currentUser = string.IsNullOrWhiteSpace(CurrentUser) ? "当前用户" : CurrentUser;

        if (!string.IsNullOrWhiteSpace(CurrentCrontab) && !LooksLikeNoCrontab(CurrentCrontab))
        {
            AddCronEntries(tasks, CurrentCrontab!, false, ScheduledTaskKind.UserCrontab,
                $"crontab -l ({currentUser})", currentUser, serverNow);
        }

        if (!string.IsNullOrWhiteSpace(ElevatedCrontab) && !LooksLikeNoCrontab(ElevatedCrontab) &&
            !string.Equals(ElevatedUser, currentUser, StringComparison.Ordinal))
        {
            AddCronEntries(tasks, ElevatedCrontab!, false, ScheduledTaskKind.UserCrontab,
                $"crontab -l ({ElevatedUser})", ElevatedUser ?? "root", serverNow);
        }

        foreach (var pair in UserCrontabs)
        {
            if (LooksLikeNoCrontab(pair.Value) ||
                string.Equals(pair.Key, currentUser, StringComparison.Ordinal) ||
                string.Equals(pair.Key, ElevatedUser, StringComparison.Ordinal))
            {
                continue;
            }

            AddCronEntries(tasks, pair.Value, false, ScheduledTaskKind.UserCrontab,
                $"crontab -l ({pair.Key})", pair.Key, serverNow);
        }

        if (!string.IsNullOrWhiteSpace(EtcCrontab))
        {
            AddCronEntries(tasks, EtcCrontab!, true, ScheduledTaskKind.SystemCrontab,
                "/etc/crontab", "root", serverNow);
        }

        foreach (var pair in CronDFiles)
        {
            AddCronEntries(tasks, pair.Value, true, ScheduledTaskKind.CronD,
                pair.Key, "root", serverNow);
        }

        foreach (var script in CronDirScripts)
        {
            var directory = script.Path[..Math.Max(0, script.Path.LastIndexOf('/'))];
            var directoryName = directory[(directory.LastIndexOf('/') + 1)..];
            var description = directoryName switch
            {
                "hourly" => "每小时（由 run-parts 调度）",
                "daily" => "每天（由 run-parts/anacron 调度）",
                "weekly" => "每周（由 run-parts/anacron 调度）",
                "monthly" => "每月（由 run-parts/anacron 调度）",
                _ => "系统目录调度"
            };
            var executable = IsExecutable(script.Mode);
            var scriptPath = script.Path;
            tasks.Add(new ScheduledTaskInfo
            {
                Id = $"cron-dir:{script.Path}",
                Kind = ScheduledTaskKind.CronDirectory,
                Owner = script.Owner,
                SourcePath = script.Path,
                LineNumber = 0,
                RawLine = string.Empty,
                ScheduleText = "run-parts",
                ScheduleDescription = description,
                Command = scriptPath,
                IsPaused = !executable,
                StateText = executable ? "已启用" : "已暂停（无执行权限）",
                NextRunText = "由系统调度",
                ScriptPath = scriptPath,
                Description = FileNameFromPath(scriptPath)
            });
        }

        foreach (var timer in Timers)
        {
            var command = string.IsNullOrWhiteSpace(timer.ServiceCommand)
                ? timer.ActivatedUnit
                : timer.ServiceCommand;
            ScheduledTaskScriptDetector.TryDetect(command, out var scriptPath, out var interpreter);
            var scheduleText = JoinScheduleText(timer);
            tasks.Add(new ScheduledTaskInfo
            {
                Id = $"systemd:{timer.Unit}",
                Kind = ScheduledTaskKind.SystemdTimer,
                Owner = timer.ServiceUser,
                SourcePath = string.IsNullOrWhiteSpace(timer.FragmentPath) ? timer.Unit : timer.FragmentPath,
                LineNumber = 0,
                RawLine = string.Empty,
                ScheduleText = scheduleText,
                ScheduleDescription = timer.ScheduleDescription,
                Command = command,
                IsPaused = !timer.IsActive,
                StateText = timer.StateText,
                NextRunText = FormatTimestamp(timer.NextElapse, serverNow.Offset, timer.NextElapseRaw),
                LastRunText = FormatTimestamp(timer.LastTrigger, serverNow.Offset, timer.LastTriggerRaw),
                ScriptPath = scriptPath.Length > 0 ? scriptPath : null,
                ScriptInterpreter = interpreter.Length > 0 ? interpreter : null,
                UnitName = timer.Unit,
                ActivatedUnit = timer.ActivatedUnit,
                UnitFileState = timer.UnitFileState,
                ActiveState = timer.ActiveState,
                FragmentPath = timer.FragmentPath,
                Description = timer.Description
            });
        }

        return tasks
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(t => t.Kind)
            .ThenBy(t => t.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.LineNumber)
            .ToList();
    }

    private void AddCronEntries(
        List<ScheduledTaskInfo> tasks,
        string text,
        bool hasUserField,
        ScheduledTaskKind kind,
        string sourcePath,
        string defaultOwner,
        DateTimeOffset serverNow)
    {
        var entries = CrontabParser.Parse(text, hasUserField, defaultOwner);
        foreach (var entry in entries)
        {
            var description = entry.Schedule?.Describe() ?? $"无法解析：{entry.ParseError}";
            var next = entry.Schedule?.GetNextOccurrences(serverNow.DateTime, 1).FirstOrDefault();
            ScheduledTaskScriptDetector.TryDetect(entry.Command, out var scriptPath, out var interpreter);
            tasks.Add(new ScheduledTaskInfo
            {
                Id = $"{kind}:{sourcePath}:{entry.LineIndex}",
                Kind = kind,
                Owner = entry.User ?? defaultOwner,
                SourcePath = sourcePath,
                LineNumber = entry.LineIndex + 1,
                RawLine = entry.RawLine,
                ScheduleText = entry.Expression,
                ScheduleDescription = description,
                Command = entry.Command,
                IsPaused = entry.IsPaused,
                StateText = entry.IsPaused ? "已暂停" : "已启用",
                NextRunText = entry.Schedule?.IsReboot == true
                    ? "系统启动时"
                    : next is null
                        ? string.Empty
                        : next.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ScriptPath = scriptPath.Length > 0 ? scriptPath : null,
                ScriptInterpreter = interpreter.Length > 0 ? interpreter : null,
                Description = BuildCronTitle(scriptPath, entry.Command)
            });
        }
    }

    private static string JoinScheduleText(SystemdTimerInfo timer)
    {
        var calendar = string.Join("; ", timer.CalendarExpressions);
        var monotonic = string.Join("; ", timer.MonotonicValues.Select(x => $"{x.Key}={x.Value}"));
        if (calendar.Length == 0)
        {
            return monotonic;
        }

        return monotonic.Length == 0 ? calendar : calendar + "; " + monotonic;
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp, TimeSpan serverOffset, string rawValue)
    {
        if (timestamp is not null)
        {
            return timestamp.Value.ToOffset(serverOffset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(rawValue) || rawValue is "n/a" or "0" ? string.Empty : rawValue;
    }

    private static bool IsExecutable(string mode)
    {
        try
        {
            var value = Convert.ToInt32(mode, 8);
            return (value & 0b001001001) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeNoCrontab(string text)
        => text.Contains("no crontab for", StringComparison.OrdinalIgnoreCase);

    private static string FileNameFromPath(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }

    private static string BuildCronTitle(string scriptPath, string command)
    {
        if (!string.IsNullOrWhiteSpace(scriptPath))
        {
            return FileNameFromPath(scriptPath);
        }

        var firstLine = command.Split('\n')[0].Trim();
        return firstLine.Length <= 80 ? firstLine : firstLine[..80] + "…";
    }

    private string GetMeta(string key) => Meta.TryGetValue(key, out var value) ? value : string.Empty;
}

public sealed class CronDirScriptInfo
{
    public string Path { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public long Size { get; init; }
    public long ModifiedUnixSeconds { get; init; }
}
