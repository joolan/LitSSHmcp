using LitSSHmcp.App.Services;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class LocalSystemMetricsServiceTests
{
    [Fact]
    public void Sample_returns_local_machine_metrics()
    {
        using var service = new LocalSystemMetricsService();

        _ = service.Sample();
        Thread.Sleep(50);
        var snapshot = service.Sample();

        Assert.Equal(Environment.MachineName, snapshot.MachineName);
        Assert.True(snapshot.CoreCount >= 1);
        Assert.True(snapshot.MemoryTotalBytes > 0);
        Assert.True(snapshot.MemoryAvailableBytes >= 0);
        Assert.InRange(snapshot.CpuPercent, 0, 100);
        Assert.InRange(snapshot.MemoryPercent, 0, 100);
        Assert.True(snapshot.Uptime > TimeSpan.Zero);
        Assert.NotEmpty(snapshot.CpuName);
        Assert.NotEmpty(snapshot.OsName);
    }
}
