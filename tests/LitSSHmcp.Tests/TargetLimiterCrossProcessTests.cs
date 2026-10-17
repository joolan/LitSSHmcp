using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>
/// TargetLimiter 跨进程层（评估报告 §4.3 / M2-1）：两个实例共享同一 sharedDirectory 即等价于
/// 两个进程（各自独立的进程内状态，仅通过槽位文件与速率窗口交互）。
/// </summary>
public class TargetLimiterCrossProcessTests : IDisposable
{
    private readonly string _sharedDir =
        Path.Combine(Path.GetTempPath(), "litssh-m2", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_sharedDir, recursive: true); }
        catch { /* 残留句柄/已删除时忽略 */ }
    }

    private static FakeSecurityOptions Limits(int concurrency, int perMinute) => new()
    {
        Limits = new LimitsConfig
        {
            MaxConcurrentPerTarget = concurrency,
            MaxCallsPerMinutePerTarget = perMinute
        }
    };

    [Fact]
    public void Shared_slots_limit_concurrency_across_instances()
    {
        var options = Limits(concurrency: 1, perMinute: 0);
        var a = new TargetLimiter(options, _sharedDir);
        var b = new TargetLimiter(options, _sharedDir);

        Assert.True(a.TryAcquire("ssh:s1", out var lease, out _));
        Assert.False(b.TryAcquire("ssh:s1", out _, out var reason));
        Assert.Equal("concurrency_limit_exceeded", reason);

        lease!.Dispose();
        Assert.True(b.TryAcquire("ssh:s1", out var lease2, out _));
        lease2!.Dispose();
    }

    [Fact]
    public void Shared_rate_window_rejects_across_instances()
    {
        var options = Limits(concurrency: 5, perMinute: 2);
        var a = new TargetLimiter(options, _sharedDir);
        var b = new TargetLimiter(options, _sharedDir);

        Assert.True(a.TryAcquire("ds:d1", out var l1, out _));
        l1!.Dispose();
        Assert.True(b.TryAcquire("ds:d1", out var l2, out _));
        l2!.Dispose();

        Assert.False(a.TryAcquire("ds:d1", out _, out var reasonA));
        Assert.Equal("rate_limit_exceeded", reasonA);
        Assert.False(b.TryAcquire("ds:d1", out _, out var reasonB));
        Assert.Equal("rate_limit_exceeded", reasonB);
    }

    [Fact]
    public void Different_targets_are_independent_across_instances()
    {
        var options = Limits(concurrency: 1, perMinute: 0);
        var a = new TargetLimiter(options, _sharedDir);
        var b = new TargetLimiter(options, _sharedDir);

        Assert.True(a.TryAcquire("ssh:s1", out var l1, out _));
        Assert.True(b.TryAcquire("ssh:s2", out var l2, out _));
        l1!.Dispose();
        l2!.Dispose();
    }

    [Fact]
    public void Fail_open_when_shared_directory_unusable()
    {
        // 用"文件"冒充共享目录：建目录必然失败 → 失败安全降级为仅进程内限流，不抛异常
        var blocker = Path.Combine(Path.GetTempPath(), "litssh-m2-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            var options = Limits(concurrency: 1, perMinute: 0);
            var limiter = new TargetLimiter(options, blocker);

            Assert.True(limiter.TryAcquire("ssh:s1", out var lease, out _));
            Assert.False(limiter.TryAcquire("ssh:s1", out _, out var reason));
            Assert.Equal("concurrency_limit_exceeded", reason);
            lease!.Dispose();
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Null_shared_directory_keeps_in_process_behavior()
    {
        // 兼容既有构造（测试/未接线路径）：不传共享目录时行为与原实现一致
        var options = Limits(concurrency: 1, perMinute: 1);
        var limiter = new TargetLimiter(options);

        Assert.True(limiter.TryAcquire("ssh:a", out var l1, out _));
        l1!.Dispose();
        Assert.False(limiter.TryAcquire("ssh:a", out _, out var reason));
        Assert.Equal("rate_limit_exceeded", reason);
    }
}
