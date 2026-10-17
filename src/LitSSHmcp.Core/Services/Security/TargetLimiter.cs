using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 按目标（服务器/数据源）限制并发与速率。超出限制时拒绝调用，避免 AI 并发压垮目标。
/// 构造时传入 <c>sharedDirectory</c> 后追加跨进程层（评估报告 §4.3/M2-1）：
/// App / 多个 MCP 子进程对同一目标共享槽位与速率窗口；共享目录不可用时失败安全降级为仅进程内限流。
/// </summary>
public interface ITargetLimiter
{
    bool TryAcquire(string targetKey, out IDisposable? lease, out string? reason);
}

public sealed class TargetLimiter : ITargetLimiter
{
    private readonly ISecurityOptionsProvider _options;
    private readonly ConcurrentDictionary<string, TargetState> _states = new();
    private readonly string? _sharedDirectory;

    public TargetLimiter(ISecurityOptionsProvider options, string? sharedDirectory = null)
    {
        _options = options;
        _sharedDirectory = sharedDirectory;
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

            // 进程内记账刻意放在跨进程闸门成功之后（见下）：被跨进程层拒绝的调用不计入本进程窗口。
            // 代价是极端并发下进程内窗口可能短暂多计 ≤ 同时在途的线程数 —— 速率本是粗粒度节流，可接受；
            // 真正的并发精确性由信号量（进程内）与槽位文件（跨进程）保证。
            if (!TryGlobalSlot(targetKey, maxConcurrent, out var slot, out reason))
            {
                state.Semaphore.Release();
                return false;
            }

            if (!TryGlobalRate(targetKey, maxPerMinute, out reason))
            {
                slot?.Dispose();
                state.Semaphore.Release();
                return false;
            }

            state.CallTimes.Enqueue(now);
            lease = new Lease(state.Semaphore, slot);
            return true;
        }
    }

    /// <summary>
    /// 跨进程并发槽位：每个目标 maxConcurrent 个 slot 文件，<c>FileShare.None</c> 独占持有，
    /// 进程崩溃由 OS 释放句柄（残留文件无害 —— 锁定的是句柄而不是文件存在性）。
    /// 共享目录不可用（磁盘/权限/路径异常）时失败安全放行，仅保留进程内限流。
    /// </summary>
    private bool TryGlobalSlot(string targetKey, int maxConcurrent, out FileStream? slot, out string? reason)
    {
        slot = null;
        reason = null;
        if (_sharedDirectory is null)
            return true;

        string dir;
        try
        {
            dir = Path.Combine(_sharedDirectory, "slots", HashKey(targetKey));
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return true; // 失败安全：共享目录坏了不能拒绝服务
        }

        for (var i = 0; i < maxConcurrent; i++)
        {
            try
            {
                slot = new FileStream(
                    Path.Combine(dir, $"slot{i}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
                return true;
            }
            catch (IOException ex) when (IsLockBusy(ex))
            {
                // 槽位被其它进程（或本进程另一实例）占用：试下一个
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true; // 非竞争性 IO/权限错误 → 失败安全放行
            }
        }

        slot = null;
        reason = "concurrency_limit_exceeded";
        return false;
    }

    /// <summary>
    /// 跨进程速率窗口：rate.lock 独占下读改写 rate.json（一分钟时间戳队列）。
    /// 锁竞争超时/IO 异常/文件损坏 → 失败安全放行（本进程窗口仍生效）。
    /// </summary>
    private bool TryGlobalRate(string targetKey, int maxPerMinute, out string? reason)
    {
        reason = null;
        if (_sharedDirectory is null || maxPerMinute <= 0)
            return true;

        try
        {
            var dir = Path.Combine(_sharedDirectory, "rate", HashKey(targetKey));
            Directory.CreateDirectory(dir);

            var gate = TryOpenBusyLock(Path.Combine(dir, "rate.lock"), TimeSpan.FromMilliseconds(300));
            if (gate is null)
                return true; // 锁竞争超时 → 失败安全放行

            using (gate)
            {
                var path = Path.Combine(dir, "rate.json");
                var calls = new List<long>();
                try
                {
                    if (File.Exists(path))
                        calls = JsonSerializer.Deserialize<List<long>>(File.ReadAllText(path)) ?? new List<long>();
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    calls = new List<long>(); // 损坏/读失败 → 重置窗口（宁松勿卡死）
                }

                var cutoff = DateTime.UtcNow.AddMinutes(-1).Ticks;
                calls.RemoveAll(t => t < cutoff);

                if (calls.Count >= maxPerMinute)
                {
                    reason = "rate_limit_exceeded";
                    return false;
                }

                calls.Add(DateTime.UtcNow.Ticks);
                File.WriteAllText(path, JsonSerializer.Serialize(calls));
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            reason = null;
            return true; // 失败安全
        }
    }

    /// <summary>非阻塞式独占打开；被占用则短暂自旋到 <paramref name="timeout"/>，超时返回 null。</summary>
    private static FileStream? TryOpenBusyLock(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException ex) when (IsLockBusy(ex))
            {
                if (DateTime.UtcNow >= deadline)
                    return null;
                Thread.Sleep(10);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>共享冲突（ERROR_SHARING_VIOLATION=32 / ERROR_LOCK_VIOLATION=33）才自旋重试；其它 IO 错误走失败安全。</summary>
    private static bool IsLockBusy(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static string HashKey(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
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
        private FileStream? _slot;

        public Lease(SemaphoreSlim semaphore, FileStream? slot)
        {
            _semaphore = semaphore;
            _slot = slot;
        }

        public void Dispose()
        {
            var semaphore = Interlocked.Exchange(ref _semaphore, null);
            semaphore?.Release();
            Interlocked.Exchange(ref _slot, null)?.Dispose();
        }
    }
}
