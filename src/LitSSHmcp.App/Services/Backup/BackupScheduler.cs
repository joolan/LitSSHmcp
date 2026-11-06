using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.Backup;

/// <summary>应用内定时备份调度：App 运行期间执行，关闭即停止，不补跑错过的计划。</summary>
public sealed class BackupScheduler : IDisposable
{
    private readonly IConfigService _config;
    private readonly BackupService _backup;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, DateTime> _next = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public BackupScheduler(IConfigService config, BackupService? backup = null)
    {
        _config = config;
        _backup = backup ?? new BackupService(new AliyunOssBackupUploader(), new DpapiSecretProtector());
    }

    public event Action<string, bool>? RunningChanged;

    public void Start() => _ = StartAsync();

    private async Task StartAsync()
    {
        await RunStartupFallbackAsync();
        await LoopAsync();
    }

    private async Task RunStartupFallbackAsync()
    {
        AppConfig config;
        try { config = await _config.LoadConfigAsync(); }
        catch { return; }

        foreach (var plan in config.Backup.Plans.Where(item => item.Enabled && item.FallbackOnStartup))
        {
            var state = BackupStateStore.Get(plan.Id);
            if (ShouldRunStartupFallback(plan, state, DateTime.Now))
                await RunPlanAsync(plan, config);
        }
    }

    public static bool IsSuccessToday(BackupState? state, DateTime? now = null) =>
        state?.LastRunAt is { } last &&
        last.Date == (now ?? DateTime.Now).Date &&
        (state.LastResult == "ok" || state.LastResult == "oss-fail");

    public static bool ShouldRunStartupFallback(BackupPlanConfig plan, BackupState? state,
        DateTime now)
    {
        if (!plan.Enabled || !plan.FallbackOnStartup || IsSuccessToday(state, now))
            return false;

        if (plan.ScheduleType == BackupScheduleType.Interval)
        {
            if (state?.LastRunAt is null)
                return true;
            return now - state.LastRunAt.Value >=
                   TimeSpan.FromMinutes(Math.Max(1, plan.IntervalMinutes));
        }

        var time = SyncSchedule.ParseTimeOfDay(plan.TimeOfDay);
        if (now.TimeOfDay < time)
            return false;

        return plan.ScheduleType switch
        {
            BackupScheduleType.Daily => true,
            BackupScheduleType.Weekly => plan.WeekDays.Contains((int)now.DayOfWeek),
            BackupScheduleType.Monthly => plan.MonthDays.Contains(now.Day),
            _ => false
        };
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch { /* 调度循环自身异常不终止 */ }

            try { await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        AppConfig config;
        try { config = await _config.LoadConfigAsync(); }
        catch { return; }

        var now = DateTime.Now;
        var plans = config.Backup.Plans.Where(plan => plan.Enabled).ToList();
        var ids = new HashSet<string>(plans.Select(plan => plan.Id), StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var stale in _next.Keys.Where(key => !ids.Contains(key)).ToList())
                _next.Remove(stale);
        }

        foreach (var plan in plans)
        {
            var state = BackupStateStore.Get(plan.Id);
            DateTime next;
            lock (_gate)
            {
                if (!_next.TryGetValue(plan.Id, out next))
                {
                    var computed = BackupSchedule.ComputeNextRun(plan, now, state?.LastRunAt);
                    if (computed is null)
                        continue;
                    _next[plan.Id] = computed.Value;
                    continue;
                }
                if (now < next)
                    continue;
            }

            await RunPlanAsync(plan, config);
        }
    }

    private async Task RunPlanAsync(BackupPlanConfig plan, AppConfig config)
    {
        lock (_gate)
        {
            if (!_running.Add(plan.Id))
                return;
        }

        RunningChanged?.Invoke(plan.Id, true);
        try
        {
            var attempts = Math.Clamp(plan.MaxRetries, 0, 5) + 1;
            BackupRunResult? final = null;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var result = await _backup.RunAsync(plan, config, ct: _cts.Token);
                final = result;
                if (result.Success)
                    break;
                if (attempt >= attempts)
                    break;
                var delay = TimeSpan.FromSeconds(Math.Min(300, 10 * Math.Pow(2, attempt - 1)));
                try { await Task.Delay(delay, _cts.Token); }
                catch (OperationCanceledException) { return; }
            }

            if (final is null)
                return;

            var state = new BackupState
            {
                LastRunAt = DateTime.Now,
                LastBackupFile = final.LocalPath,
                LastUploadResult = final.UploadMessage,
                LastResult = final.Success
                    ? final.Uploaded ? "ok" : final.UploadMessage is null ? "ok" : "oss-fail"
                    : "fail:" + final.Message
            };
            BackupStateStore.Save(plan.Id, state);
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(plan.Id);
                var state = BackupStateStore.Get(plan.Id);
                var next = BackupSchedule.ComputeNextRun(plan, DateTime.Now, state?.LastRunAt);
                if (next is null) _next.Remove(plan.Id);
                else _next[plan.Id] = next.Value;
            }
            RunningChanged?.Invoke(plan.Id, false);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
