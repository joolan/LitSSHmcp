using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>路径物理规范化防护（评估报告 §10.C.3）：junction/符号链接逃逸必须被检出。</summary>
public class PathGuardTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "litssh-pathguard-" + Guid.NewGuid().ToString("N"));

    public PathGuardTests() => Directory.CreateDirectory(_baseDir);

    private string Dir(string name)
    {
        var dir = Path.Combine(_baseDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>创建目录链接（junction）；失败返回 false（测试跳过）。</summary>
    private bool TryCreateLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null) return false;
                p.WaitForExit(5000);
                return p.ExitCode == 0 && Directory.Exists(link);
            }
            catch
            {
                return false;
            }
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteLink(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c rmdir \"{path}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(5000);
            }
            else
            {
                Directory.Delete(path, false);
            }
        }
        catch
        {
            // 忽略（临时目录由系统回收兜底）
        }
    }

    [Fact]
    public void IsUnder_allows_plain_paths_inside_root()
    {
        var root = Dir("root");
        File.WriteAllText(Path.Combine(root, "a.txt"), "x");

        Assert.True(PathGuard.IsUnder(root, Path.Combine(root, "a.txt")));
        Assert.True(PathGuard.IsUnder(root, Path.Combine(root, "sub", "b.txt")));   // 不存在的尾段按字面保留
        Assert.False(PathGuard.IsUnder(root, Path.Combine(Path.GetDirectoryName(root)!, "other.txt")));
    }

    [Fact]
    public void IsUnder_rejects_path_through_junction_pointing_outside()
    {
        var root = Dir("root2");
        var outside = Dir("outside");
        var link = Path.Combine(root, "evil");
        if (!TryCreateLink(link, outside))
            return;    // 环境不允许创建链接（如非管理员且未开开发者模式）：跳过

        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");

            // 字面看在根内，物理解析后指向根外 → 拒绝
            Assert.False(PathGuard.IsUnder(root, Path.Combine(link, "secret.txt")));
            // 同名真实文件在根内 → 允许（防护不误伤正常路径）
            File.WriteAllText(Path.Combine(root, "ok.txt"), "x");
            Assert.True(PathGuard.IsUnder(root, Path.Combine(root, "ok.txt")));
        }
        finally
        {
            DeleteLink(link);
        }
    }

    [Fact]
    public void IsUnder_allows_junction_pointing_inside_root()
    {
        var root = Dir("root3");
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        var alias = Path.Combine(root, "alias");
        if (!TryCreateLink(alias, real))
            return;

        try
        {
            Assert.True(PathGuard.IsUnder(root, Path.Combine(alias, "x.txt")));
        }
        finally
        {
            DeleteLink(alias);
        }
    }

    [Fact]
    public void IsUnder_rejects_null_or_invalid()
    {
        Assert.False(PathGuard.IsUnder(Path.Combine(_baseDir, "nonexistent-root"), (string?)null));
        Assert.False(PathGuard.IsUnder("  ", "  "));
        Assert.False(PathGuard.IsUnder((string?)null!, "x"));
    }

    public void Dispose()
    {
        try
        {
            // 先删链接（若残留），再删基目录
            if (Directory.Exists(_baseDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(_baseDir))
                {
                    var attrs = File.GetAttributes(dir);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                        DeleteLink(dir);
                }
                Directory.Delete(_baseDir, true);
            }
        }
        catch
        {
            /* ignore */
        }
    }
}
