using System.Diagnostics;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services.Backup;

public static class WindowsScheduledTaskService
{
    public static string TaskName(BackupPlanConfig plan) =>
        "LitSSHmcp-Backup-" + plan.Id;

    public static async Task<bool> IsInstalledAsync(BackupPlanConfig plan)
    {
        var info = CreateInfo();
        info.ArgumentList.Add("/Query");
        info.ArgumentList.Add("/TN");
        info.ArgumentList.Add(TaskName(plan));
        using var process = NativeShell.Start(info)
            ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }

    public static async Task InstallAsync(BackupPlanConfig plan)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取当前程序路径。");
        var info = CreateInfo();
        info.ArgumentList.Add("/Create");
        info.ArgumentList.Add("/TN");
        info.ArgumentList.Add(TaskName(plan));
        info.ArgumentList.Add("/TR");
        info.ArgumentList.Add($"\"{exe}\" --run-backup {plan.Id}");
        info.ArgumentList.Add("/F");

        switch (plan.ScheduleType)
        {
            case BackupScheduleType.Daily:
                AddSchedule(info, "/SC", "DAILY");
                AddSchedule(info, "/ST", plan.TimeOfDay);
                break;
            case BackupScheduleType.Weekly:
                AddSchedule(info, "/SC", "WEEKLY");
                AddSchedule(info, "/D", FormatWeekDays(plan.WeekDays));
                AddSchedule(info, "/ST", plan.TimeOfDay);
                break;
            case BackupScheduleType.Monthly:
                AddSchedule(info, "/SC", "MONTHLY");
                AddSchedule(info, "/D", string.Join(",", plan.MonthDays.DefaultIfEmpty(1)));
                AddSchedule(info, "/ST", plan.TimeOfDay);
                break;
            default:
                AddSchedule(info, "/SC", "MINUTE");
                AddSchedule(info, "/MO", Math.Clamp(plan.IntervalMinutes, 1, 1439).ToString());
                break;
        }

        using var process = NativeShell.Start(info)
            ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
    }

    public static async Task RemoveAsync(BackupPlanConfig plan)
    {
        var info = CreateInfo();
        info.ArgumentList.Add("/Delete");
        info.ArgumentList.Add("/TN");
        info.ArgumentList.Add(TaskName(plan));
        info.ArgumentList.Add("/F");
        using var process = NativeShell.Start(info)
            ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
    }

    private static ProcessStartInfo CreateInfo() => new("schtasks.exe")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static void AddSchedule(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(value);
    }

    private static string FormatWeekDays(int[] days)
    {
        var map = new[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };
        var selected = (days ?? Array.Empty<int>())
            .Distinct()
            .Where(day => day is >= 0 and <= 6)
            .OrderBy(day => day)
            .Select(day => map[day])
            .ToArray();
        return selected.Length == 0 ? "MON" : string.Join(",", selected);
    }
}
