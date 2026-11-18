namespace LitSSHmcp.App.Services.Toolbox;

/// <summary>应用运行期间每日定时发送网络唤醒魔术包；关闭 App 后不再执行。</summary>
public sealed class WakeOnLanScheduler : IDisposable
{
    private readonly WakeOnLanStore _store;
    private readonly WakeOnLanService _service;
    private readonly Timer _timer;
    private int _running;

    public WakeOnLanScheduler(WakeOnLanStore? store = null, WakeOnLanService? service = null)
    {
        _store = store ?? new WakeOnLanStore();
        _service = service ?? new WakeOnLanService();
        _timer = new Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            return;
        try
        {
            var document = await _store.LoadAsync().ConfigureAwait(false);
            var now = DateTime.Now;
            var today = now.ToString("yyyy-MM-dd");
            var changed = false;

            foreach (var host in document.Hosts)
            {
                if (!host.ScheduleEnabled ||
                    !WakeOnLanCsv.IsValidTime(host.ScheduleTime) ||
                    !TimeSpan.TryParseExact(host.ScheduleTime.Trim(), @"hh\:mm", null, out var scheduledTime) ||
                    string.Equals(host.LastScheduledWakeDate, today, StringComparison.Ordinal))
                    continue;

                var scheduledToday = now.Date + scheduledTime;
                // 允许调度线程繁忙/休眠后延迟几分钟补发，但不会在当天任意时刻重复触发。
                if (now < scheduledToday || now - scheduledToday > TimeSpan.FromMinutes(5))
                    continue;

                var result = await _service.WakeAsync(host).ConfigureAwait(false);
                if (!result.Success)
                    continue;

                host.LastScheduledWakeDate = today;
                host.LastWokenAt = DateTimeOffset.Now;
                host.WakeCount++;
                host.UpdatedAt = DateTimeOffset.Now;
                changed = true;
            }

            if (changed)
                await _store.SaveAsync(document).ConfigureAwait(false);
        }
        catch
        {
            // 定时唤醒失败不影响 App 其他功能；下一轮会重试。
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
