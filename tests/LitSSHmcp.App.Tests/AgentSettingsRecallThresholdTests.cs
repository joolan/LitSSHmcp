using System.IO;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 召回阈值输入框：验证界面保存语义与运行时一致（0&lt;v&lt;=1 保留，其余回退默认），
/// 以及 XAML 确实绑定到了该属性（防止以后只留 ViewModel 属性却漏了输入框）。
/// </summary>
public class AgentSettingsRecallThresholdTests
{
    [Theory]
    [InlineData("0.15", 0.15)]
    [InlineData("0.3", 0.3)]
    [InlineData("0.45", 0.45)]
    [InlineData("0.9", 0.9)]
    [InlineData("1", 1.0)]
    [InlineData(" 0.6 ", 0.6)]       // 前后空白
    public void Valid_threshold_is_kept_verbatim(string input, double expected) =>
        Assert.Equal(expected, AgentSettingsViewModel.NormalizeRecallThreshold(input), 6);

    [Theory]
    [InlineData("0")]                 // 0 不是「关闭过滤」，运行时会回退默认
    [InlineData("-0.1")]
    [InlineData("1.01")]
    [InlineData("2")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("0,45")]              // 不变文化：逗号写法不接受，回退默认
    public void Invalid_threshold_falls_back_to_default(string? input) =>
        Assert.Equal(AgentMemoryConfig.DefaultRecallThreshold,
            AgentSettingsViewModel.NormalizeRecallThreshold(input), 6);

    [Fact]
    public void Default_threshold_constant_is_shared_with_config() =>
        Assert.Equal(AgentMemoryConfig.DefaultRecallThreshold,
            LitSSHmcp.Agent.AgentMemoryService.DefaultRecallThreshold);

    [Fact]
    public void Settings_xaml_binds_recall_threshold_input()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "LitSSHmcp.App", "Views", "AgentSettingsWindow.xaml");
        var xaml = File.ReadAllText(path);

        Assert.Contains("Binding MemoryRecallThreshold", xaml);
        Assert.Contains("Binding MemoryTopK", xaml);   // 与 TopK 同行的紧凑布局仍在
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LitSSHmcp.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("未找到仓库根目录(缺少 LitSSHmcp.slnx)");
    }
}
