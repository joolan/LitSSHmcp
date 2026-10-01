using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class TargetLimiterTests
{
    [Fact]
    public void Concurrency_limit_rejects_second_until_released()
    {
        var options = new FakeSecurityOptions
        {
            Limits = new LimitsConfig { MaxConcurrentPerTarget = 1, MaxCallsPerMinutePerTarget = 0 }
        };
        var limiter = new TargetLimiter(options);

        Assert.True(limiter.TryAcquire("ssh:a", out var lease1, out _));
        Assert.False(limiter.TryAcquire("ssh:a", out _, out var reason));
        Assert.Equal("concurrency_limit_exceeded", reason);

        lease1!.Dispose();
        Assert.True(limiter.TryAcquire("ssh:a", out var lease2, out _));
        lease2!.Dispose();
    }

    [Fact]
    public void Rate_limit_rejects_after_threshold()
    {
        var options = new FakeSecurityOptions
        {
            Limits = new LimitsConfig { MaxConcurrentPerTarget = 5, MaxCallsPerMinutePerTarget = 2 }
        };
        var limiter = new TargetLimiter(options);

        Assert.True(limiter.TryAcquire("ds:x", out var l1, out _)); l1!.Dispose();
        Assert.True(limiter.TryAcquire("ds:x", out var l2, out _)); l2!.Dispose();

        Assert.False(limiter.TryAcquire("ds:x", out _, out var reason));
        Assert.Equal("rate_limit_exceeded", reason);
    }

    [Fact]
    public void Different_targets_are_independent()
    {
        var options = new FakeSecurityOptions
        {
            Limits = new LimitsConfig { MaxConcurrentPerTarget = 1, MaxCallsPerMinutePerTarget = 0 }
        };
        var limiter = new TargetLimiter(options);

        Assert.True(limiter.TryAcquire("ssh:a", out var lease, out _));
        try
        {
            Assert.True(limiter.TryAcquire("ssh:b", out var leaseB, out _));
            leaseB!.Dispose();
        }
        finally
        {
            lease!.Dispose();
        }
    }
}
