using LitSSHmcp.App.Services;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AsyncOperationTests
{
    [Fact]
    public async Task RunAsync_routes_exception_to_callback()
    {
        Exception? captured = null;

        await AsyncOperation.RunAsync("test", () => throw new InvalidOperationException("boom"),
            ex => captured = ex);

        Assert.IsType<InvalidOperationException>(captured);
        Assert.Equal("boom", captured!.Message);
    }

    [Fact]
    public async Task RunAsync_ignores_cancellation()
    {
        var called = false;

        await AsyncOperation.RunAsync("test", () => Task.FromCanceled(new CancellationToken(true)),
            _ => called = true);

        Assert.False(called);
    }
}
