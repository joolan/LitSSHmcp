using LitSSHmcp.Agent.Scheduling;
using LitSSHmcp.App.Services.Notifications;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.AiTasks;

/// <summary>
/// AI 定时任务调度器（桌面 App 宿主）：轮询到期任务，抢占租约，交给 HeadlessAgentRunner 执行。
/// 仅在 App 运行期间工作；App 启动时自然补跑到期任务，关闭即停止。
/// </summary>
public sealed class AiTaskScheduler : IDisposable
{
    private readonly AiTaskStore _store;
    private readonly HeadlessAgentRunner _runner;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public AiTaskScheduler(AiTaskStore store, HeadlessAgentRunner runner)
    {
        _store = store;
        _runner = runner;
    }

    /// <summary>某任务一次运行结束（供 UI 刷新/日志）。</summary>
    public event Action<AiTaskConfig, AiTaskRunResult>? Completed;

    public void Start() => _ = LoopAsync();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await TickAsync(_cts.Token);
            }
            catch
            {
                // 调度循环自身异常不终止
            }

            try { await Task.Delay(TimeSpan.FromSeconds(20), _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        List<AiTaskConfig> tasks;
        try
        {
            tasks = await _store.ListAsync(includeDisabled: false, ct);
        }
        catch
        {
            return;
        }

        var now = DateTime.Now;
        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProcessTaskAsync(task, now, ct);
            }
            catch
            {
                // 单个任务异常不影响其他任务
            }
        }
    }

    private async Task ProcessTaskAsync(AiTaskConfig task, DateTime now, CancellationToken ct)
    {
        var requested = task.RunRequestedAt is not null;
        var next = task.NextRunAt;

        if (!requested && next is null)
        {
            if (task.ScheduleType == AiTaskScheduleType.Once)
                return;
            var computed = AiTaskSchedule.ComputeNextRun(task, now);
            if (computed is null)
                return;
            task.NextRunAt = computed;
            await _store.UpdateAsync(task, ct);
            return;
        }

        if (!requested && next > now)
            return;

        var trigger = requested
            ? AiTaskTrigger.Manual
            : now - next!.Value > TimeSpan.FromMinutes(5)
                ? AiTaskTrigger.CatchUp
                : AiTaskTrigger.Schedule;

        if (!requested && trigger == AiTaskTrigger.CatchUp && !task.RunOnStartupIfMissed)
        {
            var skippedNext = AiTaskSchedule.ComputeNextRun(task, now);
            await _store.UpdateAfterRunAsync(task.Id, skippedNext, "skipped: missed run", null,
                task.ScheduleType == AiTaskScheduleType.Once, ct);
            return;
        }

        var leaseDuration = TimeSpan.FromSeconds(Math.Max(300, task.TimeoutSeconds + 120));
        if (!await _store.TryAcquireLeaseAsync(task.Id, _leaseOwner, leaseDuration, ct))
            return;

        await _runGate.WaitAsync(ct);
        try
        {
            var runId = await _store.BeginRunAsync(task.Id, trigger, ct);
            AiTaskRunResult result;
            try
            {
                result = await _runner.RunAsync(task, trigger, ct);
            }
            catch (OperationCanceledException)
            {
                result = new AiTaskRunResult { Success = false, Cancelled = true, Error = "已取消" };
            }
            catch (Exception ex)
            {
                result = new AiTaskRunResult { Success = false, Error = "运行失败: " + ex.Message };
            }

            var status = result.Cancelled
                ? AiTaskRunStatus.Cancelled
                : result.Success ? AiTaskRunStatus.Succeeded : AiTaskRunStatus.Failed;
            var sessionId = long.TryParse(result.SessionId, out var parsedSessionId) ? parsedSessionId : (long?)null;

            await _store.CompleteRunAsync(runId, status, sessionId, result.Summary, result.Error,
                result.InputTokens, result.OutputTokens, CancellationToken.None);

            task.LastRunAt = DateTime.Now;
            var nextRun = AiTaskSchedule.ComputeNextRun(task, task.LastRunAt.Value);
            var disable = task.ScheduleType == AiTaskScheduleType.Once;
            await _store.UpdateAfterRunAsync(task.Id, nextRun,
                result.Success ? "ok" : "fail: " + (result.Error ?? "unknown"),
                sessionId, disable, CancellationToken.None);

            Notify(task, result);
            Completed?.Invoke(task, result);
            _ = _store.PruneRunsAsync(task.Id, 50, CancellationToken.None);
        }
        finally
        {
            _runGate.Release();
            await _store.ReleaseLeaseAsync(task.Id, _leaseOwner, CancellationToken.None);
        }
    }

    private static void Notify(AiTaskConfig task, AiTaskRunResult result)
    {
        if (!task.NotifyApp)
            return;

        var severity = result.Success ? NotificationSeverity.Success : NotificationSeverity.Error;
        var text = result.Success
            ? Truncate(result.Summary, 300)
            : Truncate(result.Error ?? "运行失败", 300);
        NotificationCenterService.Add("AI 定时任务", task.Name, text, severity);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 忽略 */ }
        _cts.Dispose();
        _runGate.Dispose();
    }
}
