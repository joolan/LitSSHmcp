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
        ("ViewModels/AgentViewModel.cs", 3700),
        ("ViewModels/TopologyViewModel.cs", 2700),
        ("ViewModels/DbWorkbenchViewModel.cs", 2600),
        ("ViewModels/AgentSettingsViewModel.cs", 1350),
        ("ViewModels/MainViewModel.cs", 1400)
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

        Assert.True(Count(sources, "MessageBox.Show") <= 99, "MessageBox.Show 数量超出基线(99)，请改用 IDialogService。");
        Assert.True(Count(sources, "new ConfigService(") <= 18, "new ConfigService() 数量超出基线(18)，请注入 IConfigService。");
        Assert.True(Count(sources, "Process.Start") <= 8, "Process.Start 数量超出基线(8)，请集中到宿主服务。");
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
