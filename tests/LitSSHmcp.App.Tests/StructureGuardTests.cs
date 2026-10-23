using System.IO;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 结构治理预算门禁：允许渐进式拆分，但禁止 ViewModel/UI 耦合在拆分前继续膨胀。
/// 预算只降不升；确实需要新增时，应先完成对应拆分或显式调整预算并在评审中说明。
/// </summary>
public class StructureGuardTests
{
    private static readonly (string File, int MaxLines)[] LineBudgets =
    {
        ("ViewModels/AgentViewModel.cs", 3480),
        ("ViewModels/AgentSessionSidebar.cs", 175),
        ("ViewModels/AgentAttachmentManager.cs", 120),
        ("ViewModels/AgentSessionOrder.cs", 60),
        ("ViewModels/TopologyViewModel.cs", 2550),
        ("ViewModels/TopologyGraphItems.cs", 190),
        ("ViewModels/DbWorkbenchViewModel.cs", 2200),
        ("ViewModels/RedisKeyBrowser.cs", 580),
        ("ViewModels/WorkbenchResultView.cs", 50),
        ("ViewModels/AgentSettingsViewModel.cs", 1350),
        ("ViewModels/AgentSettingsViewModel.Memory.cs", 300),
        ("ViewModels/MainViewModel.cs", 1400),
        ("Services/IWindowService.cs", 70),
        ("Services/WpfWindowService.cs", 130)
    };

    // 已迁移到 IUserDialogService 的 VM 不得退回直接 MessageBox。
    private static readonly string[] MigratedDialogViewModels =
    {
        "MainViewModel.cs",
        "ApplicationManageViewModel.cs",
        "DatasourceManageViewModel.cs",
        "RemoteDesktopManageViewModel.cs",
        "PortForwardViewModel.cs",
        "TriggerViewModel.cs",
        "SyncViewModel.cs",
        "TopologyManageViewModel.cs",
        "SessionBatchExecViewModel.cs",
        "DbWorkbenchViewModel.cs"
    };

    [Fact]
    public void ViewModels_stay_within_line_budgets()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var (file, maxLines) in LineBudgets)
        {
            var path = Path.Combine(root, "src", "LitSSHmcp.App", file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"未找到 {file}");

            var lines = File.ReadAllLines(path).Length;
            if (lines > maxLines)
                offenders.Add($"{file}: {lines} 行 > 预算 {maxLines}");
        }

        Assert.True(offenders.Count == 0,
            "以下 ViewModel 超出结构预算，请先拆分而不是继续堆叠：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Ui_coupling_counts_do_not_grow()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "src", "LitSSHmcp.App");
        var sources = Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.True(Count(sources, "MessageBox.Show") <= 79, "MessageBox.Show 数量超出基线(79)，请改用 IUserDialogService。");
        Assert.True(Count(sources, "new ConfigService(") <= 18, "new ConfigService() 数量超出基线(18)，请注入 IConfigService。");
        Assert.True(Count(sources, "Process.Start") <= 8, "Process.Start 数量超出基线(8)，请集中到宿主服务。");
    }

    [Fact]
    public void View_models_do_not_construct_windows_directly()
    {
        var root = FindRepoRoot();
        var vmRoot = Path.Combine(root, "src", "LitSSHmcp.App", "ViewModels");

        var offenders = Directory.EnumerateFiles(vmRoot, "*.cs")
            .Where(file => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(file), @"new\s+\w*Window\b"))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "以下 ViewModel 直接创建窗口，请改走 IWindowService：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Migrated_view_models_do_not_call_message_box_directly()
    {
        var root = FindRepoRoot();
        var vmRoot = Path.Combine(root, "src", "LitSSHmcp.App", "ViewModels");

        var offenders = MigratedDialogViewModels
            .Where(name => File.ReadAllText(Path.Combine(vmRoot, name)).Contains("MessageBox.Show", StringComparison.Ordinal))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "以下 ViewModel 已迁移到 IUserDialogService，不得再直接调用 MessageBox：\n" + string.Join("\n", offenders));
    }

    private static int Count(IEnumerable<string> sources, string token) =>
        sources.Sum(text => text.Split(token, StringSplitOptions.None).Length - 1);

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

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
