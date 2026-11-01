using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Resource;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ServerMonitorViewModelTests
{
    [Fact]
    public async Task Refresh_parses_metrics_and_commands_invoke_callbacks()
    {
        var first = """
==OS==
PRETTY_NAME="Debian GNU/Linux 12"
==KERNEL==
6.1.0
==LOAD==
0.50 0.40 0.30 1/100 1234
==STAT==
cpu  100 0 50 850 0 0 0 0 0 0
==MEM==
              total        used        free      shared  buff/cache   available
Mem:           8000        5000        1000         100        2000        1500
Swap:          2000         500
==DISK==
/dev/sda1 104857600 89128960 15728640 85% /
==UP==
86400.00 100.00
==CPU==
8
""";
        var second = first.Replace("cpu  100 0 50 850", "cpu  200 0 100 1700");
        var calls = 0;
        var ssh = new FakeSshService
        {
            OutputResolver = _ => calls++ == 0 ? first : second
        };
        var analyzeMetrics = new List<ResourceMetric>();
        var aiMetrics = new List<ResourceMetric>();
        var viewModel = new ServerMonitorViewModel(
            new SshServerConfig { Name = "web01", Host = "127.0.0.1" },
            ssh,
            metric =>
            {
                analyzeMetrics.Add(metric);
                return Task.CompletedTask;
            },
            metric =>
            {
                aiMetrics.Add(metric);
                return Task.CompletedTask;
            });

        await StaTest.RunAsync(async () =>
        {
            await viewModel.RefreshAsync();
            await viewModel.RefreshAsync();

            Assert.Equal(15, viewModel.CpuPercent);
            Assert.Equal(62, viewModel.MemPercent);
            Assert.Equal("5000 / 8000 MB", viewModel.MemText);
            Assert.Equal(85, viewModel.DiskPercent);
            Assert.Equal("0.50 / 0.40 / 0.30", viewModel.LoadText.Replace("负载 ", string.Empty));
            Assert.Contains("1 天", viewModel.UptimeText);
            Assert.Contains("Debian", viewModel.SystemText);
            Assert.Contains("更新于", viewModel.StatusMessage);

            viewModel.AnalyzeCommand.Execute("Cpu");
            viewModel.AiAnalysisCommand.Execute("Memory");
            viewModel.AnalyzeCommand.Execute(ResourceMetric.Disk);

            Assert.Contains(ResourceMetric.Cpu, analyzeMetrics);
            Assert.Contains(ResourceMetric.Disk, analyzeMetrics);
            Assert.Contains(ResourceMetric.Memory, aiMetrics);
        });
    }
}
