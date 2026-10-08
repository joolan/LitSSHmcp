using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Sync;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 应用内同步调度器：仅在 App 运行期间执行（关闭 App 即停止，不补跑错过的计划）。
/// 轮询配置，按任务的下一次运行时刻触发；失败按指数退避重试（上限 5 次）；结果写同步日志。
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    private readonly IConfigService _config;
    private readonly SyncService _sync;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, DateTime> _next = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public SyncScheduler(IConfigService config)
    {
        _config = config;
        _sync = new SyncService(new FileSshKnownHostsStore(), SshHostKeyMode.Tofu, new SshService());
    }

    /// <summary>某任务正在运行时触发（供 UI 显示状态）。</summary>
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
        var tasks = config.SyncTasks.Where(t => t.Enabled).ToList();
        var ids = new HashSet<string>(tasks.Select(t => t.Id), StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var stale in _next.Keys.Where(k => !ids.Contains(k)).ToList())
                _next.Remove(stale);
        }

        foreach (var task in tasks)
        {
            // 从持久化状态取上次运行时间/结果（用于展示与「固定间隔」基准）
            var state = SyncStateStore.Get(task.Id);
            if (state is not null)
            {
                task.LastRunAt = state.LastRunAt;
                task.LastResult = state.LastResult;
            }

            DateTime next;
            lock (_gate)
            {
                if (!_next.TryGetValue(task.Id, out next))
                {
                    var computed = SyncSchedule.ComputeNextRun(task, now);
                    if (computed is null)
                        continue; // 未配置有效调度，跳过
                    _next[task.Id] = computed.Value;
                    continue;
                }
                if (now < next)
                    continue;
            }

            await RunTaskWithRetryAsync(task, config);
        }
    }

    private async Task RunTaskWithRetryAsync(SyncTaskConfig task, AppConfig config)
    {
        lock (_gate)
        {
            if (!_running.Add(task.Id))
                return; // 同一任务上次未完，跳过
        }

        RunningChanged?.Invoke(task.Id, true);
        try
        {
            var attempt = 0;
            var maxRetries = Math.Clamp(task.MaxRetries, 0, 5);
            while (true)
            {
                SyncLog.Write($"[{task.Name}] 开始同步（{task.Direction}）尝试 {attempt + 1}/{maxRetries + 1}");
                SyncResult result;
                try
                {
                    result = await _sync.RunAsync(task, config, null, _cts.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    result = new SyncResult { Success = false, Message = ex.Message, Retryable = true };
                }

                task.LastRunAt = DateTime.Now;
                task.LastResult = result.Success ? "ok" : "fail:" + result.Message;
                SyncStateStore.Save(task.Id, task.LastRunAt.Value, task.LastResult);
                SyncLog.Write($"[{task.Name}] {(result.Success ? "成功" : "失败")}: {result.Message}" +
                              $"（传输 {result.FilesTransferred}, 删除 {result.Deleted}, 用时 {result.Duration.TotalSeconds:F1}s）");

                if (result.Success || !result.Retryable || attempt >= maxRetries)
                    break;

                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, attempt - 1))); // 5,10,20,40,80... 上限 300s
                SyncLog.Write($"[{task.Name}] 将 {delay.TotalSeconds:F0}s 后重试（{attempt}/{maxRetries}）");
                try { await Task.Delay(delay, _cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(task.Id);
                var next = SyncSchedule.ComputeNextRun(task, DateTime.Now);
                if (next is null) _next.Remove(task.Id);
                else _next[task.Id] = next.Value;
            }
            RunningChanged?.Invoke(task.Id, false);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 忽略 */ }
        _cts.Dispose();
    }
}
