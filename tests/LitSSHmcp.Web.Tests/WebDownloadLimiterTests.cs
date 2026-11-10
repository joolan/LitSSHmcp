using Xunit;

namespace LitSSHmcp.Web.Tests;

public class WebDownloadLimiterTests
{
    [Fact]
    public void Enforces_per_user_limit_and_releases_slots()
    {
        var limiter = new WebDownloadLimiter();

        using var first = limiter.TryAcquire("alice", 2);
        using var second = limiter.TryAcquire("alice", 2);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(limiter.TryAcquire("alice", 2));
        Assert.NotNull(limiter.TryAcquire("bob", 1));

        first!.Dispose();
        first.Dispose();

        using var replacement = limiter.TryAcquire("alice", 2);
        Assert.NotNull(replacement);
    }

    [Fact]
    public void Rejects_empty_key_or_non_positive_limit()
    {
        var limiter = new WebDownloadLimiter();

        Assert.Null(limiter.TryAcquire(null, 1));
        Assert.Null(limiter.TryAcquire(" ", 1));
        Assert.Null(limiter.TryAcquire("alice", 0));
    }
}
