using System.Diagnostics;
using LitSSHmcp.Agent.Scheduling;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Services.AiTasks;

/// <summary>
/// 把 AI 定时任务注册到 Windows 计划任务：App 未运行时由 schtasks 拉起
/// <c>LitSSHmcp.App.exe --run-ai-task &lt;taskId&gt;</c> 执行一次。
/// 使用当前 Windows 用户运行，确保 DPAPI 凭据可解密；不注册 SYSTEM。
/// </summary>
public static class WindowsAiTaskService
{
    public static string TaskName(AiTaskConfig task) => "LitSSHmcp-AiTask-" + task.Id;

    public static async Task<bool> IsInstalledAsync(AiTaskConfig task)
    {
        var info = CreateInfo();
        info.ArgumentList.Add("/Query");
        info.ArgumentList.Add("/TN");
        info.ArgumentList.Add(TaskName(task));
        using var process = NativeShell.Start(info)
            ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }

    public static async Task InstallAsync(AiTaskConfig task)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取当前程序路径。");
        var info = CreateInfo();
        info.ArgumentList.Add("/Create");
        info.ArgumentList.Add("/TN");
        info.ArgumentList.Add(TaskName(task));
        info.ArgumentList.Add("/TR");
        info.ArgumentList.Add($"\"{exe}\" --run-ai-task {task.Id}");
        info.ArgumentList.Add("/F");

        switch (task.ScheduleType)
        {
            case AiTaskScheduleType.Once:
                AddSchedule(info, "/SC", "ONCE");
                AddSchedule(info, "/ST", task.TimeOfDay);
                if (task.OnceAt is { } once)
                    AddSchedule(info, "/SD", once.ToString("yyyy/MM/dd"));
                break;
            case AiTaskScheduleType.Interval:
                AddSchedule(info, "/SC", "MINUTE");
                AddSchedule(info, "/MO", Math.Clamp(task.IntervalMinutes, 5, 1439).ToString());
                break;
            case AiTaskScheduleType.Weekly:
                AddSchedule(info, "/SC", "WEEKLY");
                AddSchedule(info, "/D", FormatWeekDays(task.WeekDays));
                AddSchedule(info, "/ST", task.TimeOfDay);
                break;
            case AiTaskScheduleType.Monthly:
                AddSchedule(info, "/SC", "MONTHLY");
                AddSchedule(info, "/D", string.Join(",", (task.MonthDays ?? Array.Empty<int>())
                    .Distinct().Where(d => d is >= 1 and <= 28).OrderBy(d => d)));
                AddSchedule(info, "/ST", task.TimeOfDay);
                break;
            default:
                AddSchedule(info, "/SC", "DAILY");
                AddSchedule(info, "/ST", task.TimeOfDay);
                break;
        }

        using var process = NativeShell.Start(info)
            ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
    }

    public static async Task RemoveAsync(AiTaskConfig task)
    {
        var info = CreateInfo();
        info.ArgumentList.Add("/Delete");
        info.ArgumentList.Add("/TN");
        info.ArgumentList.Add(TaskName(task));
        info.ArgumentList.Add("/F");
        using var process = NativeShell.Start(info)
            ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
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

    private static string FormatWeekDays(int[]? days)
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
