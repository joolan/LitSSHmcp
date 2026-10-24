using System.IO;
using System.Reflection;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>审计窗口 XAML：命令/SQL 两表均展示「用户」列（UserId 归因，空=local）。</summary>
public class AuditWindowXamlTests
{
    [Fact]
    public void Audit_tables_show_user_column()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "LitSSHmcp.App", "Views", "AuditWindow.xaml"));

        Assert.Contains("Header=\"用户\"", source);
        Assert.Equal(2, CountOccurrences(source, "DisplayMemberBinding=\"{Binding UserDisplay}\""));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "LitSSHmcp.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("未找到仓库根（LitSSHmcp.slnx）");
    }
}
