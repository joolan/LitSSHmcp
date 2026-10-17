using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>
/// 目标级写互斥（评估报告 §4.3 / M2-1）：两个实例共享同一锁根目录即等价于两个进程。
/// </summary>
public class FileTargetWriteMutexTests : IDisposable
{
    private readonly string _root;
    private readonly FileTargetWriteMutex _a;
    private readonly FileTargetWriteMutex _b;

    public FileTargetWriteMutexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "litssh-m2-write", Guid.NewGuid().ToString("N"));
        _a = new FileTargetWriteMutex(_root);
        _b = new FileTargetWriteMutex(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 残留句柄/已删除时忽略 */ }
    }

    [Fact]
    public async Task Held_lock_rejects_other_instance_until_released()
    {
        var (lease, reason) = await _a.AcquireAsync("ssh:s1", TimeSpan.Zero);
        Assert.NotNull(lease);
        Assert.Null(reason);

        var (busy, busyReason) = await _b.AcquireAsync("ssh:s1", TimeSpan.Zero);
        Assert.Null(busy);
        Assert.Equal("write_busy", busyReason);

        lease!.Dispose();

        var (after, afterReason) = await _b.AcquireAsync("ssh:s1", TimeSpan.Zero);
        Assert.NotNull(after);
        Assert.Null(afterReason);
        after!.Dispose();
    }

    [Fact]
    public async Task Wait_times_out_while_lock_is_held()
    {
        var (lease, _) = await _a.AcquireAsync("ssh:s1", TimeSpan.Zero);
        Assert.NotNull(lease);

        var started = DateTime.UtcNow;
        var (busy, reason) = await _b.AcquireAsync("ssh:s1", TimeSpan.FromMilliseconds(300));
        var elapsed = DateTime.UtcNow - started;

        Assert.Null(busy);
        Assert.Equal("write_busy", reason);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(200), $"应在等待上限附近超时, 实际 {elapsed.TotalMilliseconds:0}ms");

        lease!.Dispose();
    }

    [Fact]
    public async Task Different_targets_do_not_block()
    {
        var (lease, _) = await _a.AcquireAsync("ssh:s1", TimeSpan.Zero);
        Assert.NotNull(lease);

        var (other, reason) = await _b.AcquireAsync("ssh:s2", TimeSpan.Zero);
        Assert.NotNull(other);
        Assert.Null(reason);

        other!.Dispose();
        lease!.Dispose();
    }

    [Fact]
    public async Task Acquire_after_full_release_succeeds()
    {
        var (first, _) = await _a.AcquireAsync("ds:d1", TimeSpan.Zero);
        first!.Dispose();

        var (second, reason) = await _b.AcquireAsync("ds:d1", TimeSpan.Zero);
        Assert.NotNull(second);
        Assert.Null(reason);
        second!.Dispose();
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await _a.AcquireAsync("ssh:s1", TimeSpan.FromMilliseconds(100), cts.Token));
    }
}
