using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AsyncBoundaryTests
{
    [Fact]
    public void App_source_has_no_async_void_methods()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "src", "LitSSHmcp.App");
        var pattern = new Regex(@"\basync\s+void\b", RegexOptions.Compiled);

        var offenders = Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) is false)
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) is false)
            .Where(f => Path.GetFileName(f) != "AsyncOperation.cs")
            .Where(f => pattern.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToArray();

        Assert.Empty(offenders);
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

        throw new DirectoryNotFoundException("未找到仓库根目录(含 LitSSHmcp.slnx)");
    }
}
