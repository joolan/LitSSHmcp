using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    private const int MaxPooledRuntimes = 8;
    private static readonly TimeSpan RuntimeIdleTtl = TimeSpan.FromMinutes(10);
    private bool _runtimePoolTrimRunning;

    /// <summary>
    /// 有界 runtime 池：保留最近 8 个会话和 10 分钟空闲窗口；运行中/等待审批、
    /// 有未发送队列、当前显示会话不驱逐，保证切换体验和任务连续性。
    /// </summary>
    internal async Task TrimRuntimePoolAsync()
    {
        if (_runtimePoolTrimRunning)
            return;

        _runtimePoolTrimRunning = true;
        using var perf = PerfLog.Scope("agent.runtime.trim");
        try
        {
            var now = DateTime.UtcNow;
            var candidates = _contexts.Values
                .Where(ctx => ctx.Runtime is not null
                              && !ctx.IsBusy
                              && ctx.SessionId != _sessionId
                              && ctx.PendingMessages.Count == 0
                              && !ctx.QueuePaused)
                .OrderBy(ctx => ctx.LastUsedUtc)
                .ToList();

            foreach (var ctx in candidates)
            {
                var overLimit = _contexts.Count > MaxPooledRuntimes;
                var idleExpired = now - ctx.LastUsedUtc >= RuntimeIdleTtl;
                if (!overLimit && !idleExpired)
                    break;

                await RemoveContextAsync(ctx.SessionId);
            }
        }
        finally
        {
            _runtimePoolTrimRunning = false;
        }
    }

    /// <summary>显式释放非当前、非运行中的 runtime（例如设置页手动清理/测试）。</summary>
    public async Task TrimRuntimePoolNowAsync() => await TrimRuntimePoolAsync();
}
