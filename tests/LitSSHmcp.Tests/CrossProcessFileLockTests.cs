using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

public class CrossProcessFileLockTests
{
    private static string NewPath() =>
        Path.Combine(Path.GetTempPath(), "litssh-lock-" + Guid.NewGuid().ToString("N") + ".lock");

    [Fact]
    public async Task Second_acquire_waits_until_first_is_released()
    {
        var path = NewPath();
        try
        {
            var first = await CrossProcessFileLock.AcquireAsync(path, TimeSpan.FromSeconds(5));

            var second = CrossProcessFileLock.AcquireAsync(path, TimeSpan.FromSeconds(5));
            await Task.Delay(300);
            Assert.False(second.IsCompleted, "锁被持有时第二次获取不应成功");

            first.Dispose();
            var released = await second;
            released.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Acquire_throws_timeout_when_lock_is_never_released()
    {
        var path = NewPath();
        try
        {
            var held = await CrossProcessFileLock.AcquireAsync(path, TimeSpan.FromSeconds(5));
            try
            {
                await Assert.ThrowsAsync<TimeoutException>(() =>
                    CrossProcessFileLock.AcquireAsync(path, TimeSpan.FromMilliseconds(200)));
            }
            finally
            {
                held.Dispose();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
