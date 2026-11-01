using System.Reflection;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Resource;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ResourceAnalysisPromptTests
{
    [Fact]
    public async Task Ai_prompts_focus_on_the_selected_metric()
    {
        var server = new SshServerConfig { Name = "test", Host = "127.0.0.1" };
        var viewModel = new ResourceAnalysisViewModel(
            server,
            AppServiceFactory.CreateSshService(),
            new UserDialogService());

        var report = new ResourceAnalysisReport
        {
            HostName = "web01",
            OsName = "Debian",
            Kernel = "6.1",
            CpuCores = 8,
            CpuPercent = 80,
            Load1 = 7,
            MemoryTotalMb = 16000,
            MemoryUsedMb = 13000,
            MemoryAvailableMb = 3000,
            MemoryUsedPercent = 81,
            SwapTotalMb = 4096,
            SwapUsedMb = 900,
            SwapUsedPercent = 22,
            RawText = "##meta\nweb01\n##stat1\ncpu 1 0 1 8 0 0 0 0 0 0\n##stat2\ncpu 2 0 2 16 0 0 0 0 0 0\n##load\n1 1 1\n##mem\nMem: 16000 13000 1000 0 2000 3000\n##df\n/dev/sda1 100G 85G 15G 85% /\n##dfi\n/dev/sda1 100 85 15 85% /\n##mounts\n/ rw\n##ps\n123 app 45 18 2300 01:00 java\n##vmstat\nvmstat\n##psi\nsome avg10=1\n##iostat\niostat\n##journal\njournal\n##docker\ndocker\n##du\n1024\t/var\n##large\n1024\t/var/a.log\n##deleted\njava\t123\t1024\t/var/a.log\n"
        };
        report.TopCpuProcesses.Add(new ResourceProcessItem
        {
            Pid = 123,
            User = "app",
            CpuPercent = 45,
            MemPercent = 18,
            RssMb = 2300,
            Command = "java -jar app.jar"
        });
        report.TopMemoryProcesses.Add(report.TopCpuProcesses[0]);
        report.Disks.Add(new ResourceDiskItem
        {
            Filesystem = "/dev/sda1",
            Mount = "/",
            Size = "100G",
            Used = "85G",
            Available = "15G",
            UsedPercent = 85
        });

        ResourceAnalysisAdvisor.Apply(report);
        typeof(ResourceAnalysisViewModel)
            .GetField("_report", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, report);

        var cpu = await viewModel.BuildAiPromptAsync(ResourceMetric.Cpu);
        Assert.NotNull(cpu);
        Assert.Contains("CPU 占用", cpu);
        Assert.Contains("CPU 使用率", cpu);
        Assert.Contains("Top CPU 进程", cpu);
        Assert.DoesNotContain("内存使用：", cpu);
        Assert.DoesNotContain("磁盘：", cpu);
        Assert.DoesNotContain("##mem", cpu);
        Assert.Contains("##stat1", cpu);

        var memory = await viewModel.BuildAiPromptAsync(ResourceMetric.Memory);
        Assert.NotNull(memory);
        Assert.Contains("内存占用", memory);
        Assert.Contains("内存使用：", memory);
        Assert.Contains("Top 内存进程", memory);
        Assert.DoesNotContain("Top CPU 进程", memory);
        Assert.DoesNotContain("磁盘：", memory);
        Assert.Contains("##mem", memory);
        Assert.DoesNotContain("##df", memory);

        var disk = await viewModel.BuildAiPromptAsync(ResourceMetric.Disk);
        Assert.NotNull(disk);
        Assert.Contains("磁盘占用", disk);
        Assert.Contains("挂载点", disk);
        Assert.DoesNotContain("Top CPU 进程", disk);
        Assert.DoesNotContain("内存使用：", disk);
        Assert.Contains("##df", disk);
        Assert.DoesNotContain("##mem", disk);

        var overview = await viewModel.BuildAiPromptAsync(ResourceMetric.Overview);
        Assert.NotNull(overview);
        Assert.Contains("整体资源占用", overview);
        Assert.Contains("CPU 使用率", overview);
        Assert.Contains("内存使用：", overview);
        Assert.Contains("挂载点", overview);
        Assert.Contains("##mem", overview);
        Assert.Contains("##df", overview);
    }
}
