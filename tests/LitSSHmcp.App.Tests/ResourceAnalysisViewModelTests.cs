using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Resource;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ResourceAnalysisViewModelTests
{
    private const string Quick = """
##meta
web01
2026-01-01T10:00:00+0800
OS=Debian GNU/Linux 12
6.1.0
8
model name : Intel Xeon
##stat1
cpu  100 0 50 850 0 0 0 0 0 0
##load
1.50 1.20 1.00 1/100 1234
##mem
              total        used        free      shared  buff/cache   available
Mem:           8000        5000        1000         100        2000        1500
Swap:          2000         500
##df
Filesystem      Size  Used Avail Use% Mounted on
/dev/sda1       100G   85G   15G  85% /
##dfi
Filesystem      Inodes IUsed IFree IUse% Mounted on
/dev/sda1         1000   850   150  85% /
##mounts
/ rw,relatime
##ps
123 root 80.0 10.0 1048576 01:00:00 java -jar app.jar
456 app 10.0 5.0 524288 00:10:00 nginx
##vmstat
procs -----------memory---------- ---swap-- -----io---- -system-- ------cpu-----
 1  0  1000  2000  0  0    0    0    1    1  2  1  95  0  0
##stat2
cpu  200 0 100 1700 0 0 0 0 0 0
##psi
some avg10=1.00 avg60=0.50 avg300=0.20 total=123
##oom
##journal
Archived and active journals take up 100.0M in the file system.
##docker
TYPE TOTAL ACTIVE SIZE RECLAIMABLE
##end
""";

    private const string Attribution = """
##cgroups
##pid:123
0::/system.slice/app.service
Name:java
VmSwap: 1024 kB
##pid:456
0::/docker/abcdef1234567890
Name:nginx
VmSwap: 0 kB
##dockerps
abcdef123456 web-container
##end
""";

    private const string Deep = """
##du
1048576	/var
524288	/var/log
##large
2147483648	/var/log/app.log
##deleted
java	123	1073741824	/var/log/deleted.log
##journal
Archived and active journals take up 100.0M in the file system.
##docker
TYPE TOTAL ACTIVE SIZE RECLAIMABLE
##end
""";

    [Fact]
    public async Task Refresh_and_deep_scan_populate_report_and_ai_prompt()
    {
        var ssh = new FakeSshService
        {
            OutputResolver = command => command.Contains("##cgroups", StringComparison.Ordinal)
                ? Attribution
                : command.Contains("##du", StringComparison.Ordinal) ? Deep : Quick
        };
        var sent = false;
        var viewModel = new ResourceAnalysisViewModel(
            new SshServerConfig { Name = "web01", Host = "127.0.0.1" },
            ssh,
            new FakeDialogService(),
            (_, _) =>
            {
                sent = true;
                return Task.FromResult(true);
            });

        await StaTest.RunAsync(async () =>
        {
            await viewModel.RefreshAsync();

            Assert.NotNull(viewModel.Report);
            Assert.Equal(15, viewModel.Report!.CpuPercent);
            Assert.Equal(8, viewModel.Report.CpuCores);
            Assert.Equal(2, viewModel.TopCpuProcesses.Count);
            Assert.Single(viewModel.Disks);
            Assert.Equal("app", viewModel.TopCpuProcesses[0].Service);
            Assert.Equal("web-container", viewModel.TopCpuProcesses[1].Container);
            Assert.Contains("CPU", viewModel.OverviewDetail + viewModel.Report.GetSummary(ResourceMetric.Cpu).Detail);

            viewModel.CurrentMetric = ResourceMetric.Memory;
            Assert.Contains("内存", viewModel.CurrentHeadline);
            Assert.Contains("warning", viewModel.CurrentSummary.Severity.ToString(), StringComparison.OrdinalIgnoreCase);

            await viewModel.DeepScanAsync();

            Assert.True(viewModel.Report.DeepScanned);
            Assert.Equal(2, viewModel.TopDirectories.Count);
            Assert.Single(viewModel.LargeFiles);
            Assert.Single(viewModel.DeletedFiles);
            Assert.Contains("已删除", string.Join(" ", viewModel.Findings.Select(f => f.Title)));

            var diskPrompt = await viewModel.BuildAiPromptAsync(ResourceMetric.Disk);
            Assert.NotNull(diskPrompt);
            Assert.Contains("磁盘占用", diskPrompt);
            Assert.Contains("##df", diskPrompt);
            Assert.DoesNotContain("##mem", diskPrompt);

            Assert.True(await viewModel.SendPromptToAgentAsync("prompt", true));
            Assert.True(sent);
        });
    }
}
