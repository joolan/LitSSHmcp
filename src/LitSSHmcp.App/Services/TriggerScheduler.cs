using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Sync;
using LitSSHmcp.Core.Services.Trigger;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 应用内触发事件调度器：仅在 App 运行期间执行（关闭 App 即停止，不补跑错过的计划）。
/// 轮询配置，按触发事件计划触发（固定间隔 / 每周 / 每月），并订阅 SyncScheduler.TaskCompleted 做同步联动；
/// 失败按指数退避重试（上限 5 次）；结果由 TriggerRunner 写入状态与日志。
/// </summary>
public sealed class TriggerScheduler : IDisposable
{
    private readonly IConfigService _config;
    private readonly TriggerRunner _runner;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, DateTime> _next = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public TriggerScheduler(IConfigService config, TriggerRunner? runner = null)
    {
        _config = config;
        _runner = runner ?? new TriggerRunner(AppServiceFactory.CreateSshService(), new AuditLogService(), TriggerStateStore.Default);
    }

    /// <summary>某触发事件正在运行时触发（供 UI 显示状态）。</summary>
    public event Action<string, bool>? RunningChanged;

    public void Start() => _ = LoopAsync();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await TickAsync();
            }
            catch
            {
                // 调度循环自身异常不终止
            }

            try { await Task.Delay(TimeSpan.FromSeconds(20), _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        AppConfig config;
        try { config = await _config.LoadConfigAsync(); }
        catch { return; }

        var now = DateTime.Now;
        var triggers = (config.Triggers ?? Array.Empty<TriggerConfig>())
            .Where(t => t.Enabled && t.ScheduleEnabled)
            .ToList();
        var ids = new HashSet<string>(triggers.Select(t => t.Id), StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var stale in _next.Keys.Where(k => !ids.Contains(k)).ToList())
                _next.Remove(stale);
        }

        foreach (var trigger in triggers)
        {
            var state = TriggerStateStore.Default.Get(trigger.Id);
            if (state is not null)
            {
                trigger.LastRunAt = state.LastRunAt;
                trigger.LastResult = state.LastResult;
            }

            DateTime next;
            lock (_gate)
            {
                if (!_next.TryGetValue(trigger.Id, out next))
                {
                    var computed = ComputeNext(trigger, now);
                    if (computed is null)
                        continue; // 未配置有效调度，跳过
                    _next[trigger.Id] = computed.Value;
                    continue;
                }
                if (now < next)
                    continue;
            }

            await RunTriggerWithRetryAsync(trigger, config, TriggerRunCause.Schedule);
        }
    }

    /// <summary>同步任务完成后联动触发（由 SyncScheduler.TaskCompleted 事件调用）。</summary>
    public void OnSyncCompleted(string syncTaskId, SyncResult syncResult)
        => _ = RunSyncHookAsync(syncTaskId, syncResult);

    private async Task RunSyncHookAsync(string syncTaskId, SyncResult syncResult)
    {
        AppConfig config;
        try { config = await _config.LoadConfigAsync(); }
        catch { return; }

        // 同步任务里声明的「触发事件」及条件（仅成功 / 无条件）
        var task = (config.SyncTasks ?? Array.Empty<SyncTaskConfig>()).FirstOrDefault(t => t.Id == syncTaskId);
        if (task?.EventHooks is not { Length: > 0 } hooks)
            return;

        foreach (var hook in hooks)
        {
            if (hook.OnlyOnSuccess && !syncResult.Success)
                continue;

            var trigger = (config.Triggers ?? Array.Empty<TriggerConfig>())
                .FirstOrDefault(t => t.Id == hook.TriggerId && t.Enabled && t.AllowEventTrigger);
            if (trigger is null)
                continue;

            await RunTriggerWithRetryAsync(trigger, config, TriggerRunCause.Sync);
        }
    }

    private async Task RunTriggerWithRetryAsync(TriggerConfig trigger, AppConfig config, TriggerRunCause cause)
    {
        lock (_gate)
        {
            if (!_running.Add(trigger.Id))
                return; // 同一触发事件上次未完，跳过
        }

        RunningChanged?.Invoke(trigger.Id, true);
        try
        {
            var attempt = 0;
            var maxRetries = Math.Clamp(trigger.MaxRetries, 0, 5);
            while (true)
            {
                TriggerRunResult result;
                try
                {
                    result = await _runner.RunAsync(trigger, cause, config, _cts.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    result = new TriggerRunResult { Success = false, Message = "fail:" + ex.Message, StartedAt = DateTime.Now };
                }

                trigger.LastRunAt = result.StartedAt;
                trigger.LastResult = result.Message;

                if (result.Success || attempt >= maxRetries)
                    break;

                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, attempt - 1)));
                TriggerLog.Write($"[{trigger.Name}] 将 {delay.TotalSeconds:F0}s 后重试（{attempt}/{maxRetries}）");
                try { await Task.Delay(delay, _cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(trigger.Id);
                var next = ComputeNext(trigger, DateTime.Now);
                if (next is null) _next.Remove(trigger.Id);
                else _next[trigger.Id] = next.Value;
            }
            RunningChanged?.Invoke(trigger.Id, false);
        }
    }

    private static DateTime? ComputeNext(TriggerConfig trigger, DateTime now)
        => SyncSchedule.ComputeNextRun(trigger.ScheduleType, trigger.IntervalMinutes, trigger.WeekDays,
            trigger.MonthDays, trigger.TimeOfDay, trigger.LastRunAt, now);

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 忽略 */ }
        _cts.Dispose();
    }
}
