using System.Collections.Concurrent;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 按目标（服务器/数据源）限制并发与速率。超出限制时拒绝调用，避免 AI 并发压垮目标。
/// </summary>
public interface ITargetLimiter
{
    bool TryAcquire(string targetKey, out IDisposable? lease, out string? reason);
}

public sealed class TargetLimiter : ITargetLimiter
{
    private readonly ISecurityOptionsProvider _options;
    private readonly ConcurrentDictionary<string, TargetState> _states = new();

    public TargetLimiter(ISecurityOptionsProvider options)
    {
        _options = options;
    }

    public bool TryAcquire(string targetKey, out IDisposable? lease, out string? reason)
    {
        lease = null;
        reason = null;

        var limits = _options.Limits;
        var maxConcurrent = Math.Max(1, limits.MaxConcurrentPerTarget);
        var maxPerMinute = Math.Max(0, limits.MaxCallsPerMinutePerTarget);

        var state = _states.GetOrAdd(targetKey, _ => new TargetState(maxConcurrent));

        lock (state.Gate)
        {
            state.EnsureCapacity(maxConcurrent);

            var now = DateTime.UtcNow;
            while (state.CallTimes.Count > 0 && now - state.CallTimes.Peek() > TimeSpan.FromMinutes(1))
                state.CallTimes.Dequeue();

            if (maxPerMinute > 0 && state.CallTimes.Count >= maxPerMinute)
            {
                reason = "rate_limit_exceeded";
                return false;
            }

            if (!state.Semaphore.Wait(0))
            {
                reason = "concurrency_limit_exceeded";
                return false;
            }

            state.CallTimes.Enqueue(now);
            lease = new Lease(state.Semaphore);
            return true;
        }
    }

    private sealed class TargetState
    {
        public TargetState(int capacity)
        {
            Semaphore = new SemaphoreSlim(capacity, capacity);
            Capacity = capacity;
        }

        public object Gate { get; } = new();
        public SemaphoreSlim Semaphore { get; private set; }
        public int Capacity { get; private set; }
        public Queue<DateTime> CallTimes { get; } = new();

        public void EnsureCapacity(int capacity)
        {
            if (Capacity == capacity)
                return;

            var old = Semaphore;
            Semaphore = new SemaphoreSlim(capacity, capacity);
            Capacity = capacity;
            old.Dispose();
        }
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Lease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            var semaphore = Interlocked.Exchange(ref _semaphore, null);
            semaphore?.Release();
        }
    }
}
