using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class PerformanceDiagnosticsViewModelTests
{
    [Fact]
    public void Refresh_collects_process_and_gc_metrics()
    {
        using var vm = new PerformanceDiagnosticsViewModel();
        vm.Refresh();

        Assert.Contains("PID", vm.Summary);
        Assert.Contains(vm.Metrics, m => m.Name == "工作集");
        Assert.Contains(vm.Metrics, m => m.Name == "托管堆");
        Assert.Contains(vm.Metrics, m => m.Name == "MCP 宿主池");
        Assert.False(string.IsNullOrWhiteSpace(vm.LastUpdatedText));
    }
}
