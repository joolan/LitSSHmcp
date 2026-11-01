using LitSSHmcp.Core.Services.Resource;
using Xunit;

namespace LitSSHmcp.Tests;

public class ResourceAnalysisParserTests
{
    private const string QuickOutput = """
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
##psi
some avg10=1.00 avg60=0.50 avg300=0.20 total=123
##oom
##journal
Archived and active journals take up 100.0M in the file system.
##docker
TYPE TOTAL ACTIVE SIZE RECLAIMABLE
##stat2
cpu  200 0 100 1700 0 0 0 0 0 0
##end
""";

    [Fact]
    public void Parses_quick_snapshot()
    {
        var report = ResourceAnalysisParser.ParseQuick(QuickOutput);

        Assert.Equal("web01", report.HostName);
        Assert.Equal(8, report.CpuCores);
        Assert.Equal(15, report.CpuPercent);
        Assert.Equal(1.5, report.Load1);
        Assert.Equal(81.2, report.MemoryUsedPercent);
        Assert.Equal(25, report.SwapUsedPercent);
        var disk = Assert.Single(report.Disks);
        Assert.Equal("/", disk.Mount);
        Assert.Equal(85, disk.UsedPercent);
        Assert.Equal(85, disk.InodePercent);
        Assert.Equal(2, report.TopCpuProcesses.Count);
        Assert.Equal(123, report.TopCpuProcesses[0].Pid);
        Assert.Equal(1024.0, report.TopCpuProcesses[0].RssMb);
    }

    [Fact]
    public void Applies_attribution()
    {
        var report = ResourceAnalysisParser.ParseQuick(QuickOutput);

        const string attribution = """
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

        ResourceAnalysisParser.ApplyAttribution(report, attribution);

        Assert.Equal("app", report.TopCpuProcesses.First(p => p.Pid == 123).Service);
        Assert.Equal(1.0, report.TopCpuProcesses.First(p => p.Pid == 123).SwapMb);
        Assert.Equal("web-container", report.TopCpuProcesses.First(p => p.Pid == 456).Container);
    }

    [Fact]
    public void Applies_deep_scan_and_advisor_findings()
    {
        var report = ResourceAnalysisParser.ParseQuick(QuickOutput);
        ResourceAnalysisAdvisor.Apply(report);

        const string deep = """
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

        ResourceAnalysisParser.ApplyDeepScan(report, deep);
        ResourceAnalysisAdvisor.Apply(report);

        Assert.True(report.DeepScanned);
        Assert.Equal(1024.0, report.TopDirectories[0].SizeMb);
        Assert.Equal(2048.0, report.LargeFiles[0].SizeMb);
        Assert.Equal(1024.0, report.DeletedFiles[0].SizeMb);
        Assert.Contains(report.Findings, f => f.Title.Contains("已删除但仍被占用"));
        Assert.True(report.GetSummary(ResourceMetric.Disk).Severity >= ResourceSeverity.Warning);
        Assert.True(report.GetSummary(ResourceMetric.Memory).Severity >= ResourceSeverity.Warning);
        Assert.True(report.GetSummary(ResourceMetric.Overview).Severity >= ResourceSeverity.Warning);
    }
}
