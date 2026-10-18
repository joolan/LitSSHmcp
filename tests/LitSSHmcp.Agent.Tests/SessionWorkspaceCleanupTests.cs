using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// 会话目录随会话删除清理（评估报告 §5.3.5 / M1-7）：
/// 仅允许删除共享区 jobs/ 子树内的会话目录；共享根本身、jobs 根、界外目录、经 junction 指向他处的路径一律拒绝。
/// </summary>
public class SessionWorkspaceCleanupTests : IDisposable
{
    private readonly string _shared;
    private readonly string _outside;

    public SessionWorkspaceCleanupTests()
    {
        _shared = Path.Combine(Path.GetTempPath(), "litssh-cleanup-shared-" + Guid.NewGuid().ToString("N"));
        _outside = Path.Combine(Path.GetTempPath(), "litssh-cleanup-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { Directory.Delete(_shared, true); } catch { /* ignore */ }
        try { Directory.Delete(_outside, true); } catch { /* ignore */ }
    }

    private string MakeSessionDir(string name)
    {
        var dir = Path.Combine(_shared, "jobs", "local", name);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "b");
        return dir;
    }

    [Fact]
    public void Removes_session_dir_with_files_under_jobs()
    {
        var sessionDir = MakeSessionDir("s1");

        var (removed, files, error) = WorkspaceLayout.TryRemoveSessionDir(_shared, sessionDir);

        Assert.True(removed);
        Assert.Null(error);
        Assert.Equal(2, files);
        Assert.False(Directory.Exists(sessionDir));
    }

    [Fact]
    public void Refuses_shared_root_jobs_root_and_outside_dirs()
    {
        var jobsRoot = Path.Combine(_shared, "jobs");
        Directory.CreateDirectory(jobsRoot);
        var outsideDir = Path.Combine(_outside, "victim");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "keep.txt"), "keep");

        var (sharedRoot, _, sharedError) = WorkspaceLayout.TryRemoveSessionDir(_shared, _shared);
        Assert.False(sharedRoot);
        Assert.NotNull(sharedError);
        Assert.True(Directory.Exists(_shared));

        var (jobs, _, jobsError) = WorkspaceLayout.TryRemoveSessionDir(_shared, jobsRoot);
        Assert.False(jobs);
        Assert.NotNull(jobsError);
        Assert.True(Directory.Exists(jobsRoot));

        var (outside, _, outsideError) = WorkspaceLayout.TryRemoveSessionDir(_shared, outsideDir);
        Assert.False(outside);
        Assert.NotNull(outsideError);
        Assert.True(File.Exists(Path.Combine(outsideDir, "keep.txt")));
    }

    [Fact]
    public void Nonexistent_session_dir_returns_clean_no_error()
    {
        var (removed, files, error) = WorkspaceLayout.TryRemoveSessionDir(_shared,
            Path.Combine(_shared, "jobs", "local", "s-none"));

        Assert.False(removed);
        Assert.Equal(0, files);
        Assert.Null(error);
    }

    [Fact]
    public void Junction_session_dir_is_refused_and_target_preserved()
    {
        // 会话目录是经 jobs/ 指向外部的 junction：物理解析后落在 jobs/ 外 → 拒绝, 不删链接也不动目标
        var target = Path.Combine(_outside, "link-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "precious.txt"), "x");
        var link = Path.Combine(_shared, "jobs", "local", "s99");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TryCreateDirectoryLink(link, target))
            return;   // 链接创建失败(权限/平台): 跳过

        var (removed, _, error) = WorkspaceLayout.TryRemoveSessionDir(_shared, link);

        Assert.False(removed);
        Assert.NotNull(error);
        Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
    }

    /// <summary>创建目录链接(junction/符号链接)；失败返回 false（测试跳过）。</summary>
    private static bool TryCreateDirectoryLink(string link, string target)
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
                p?.WaitForExit(5000);
                return Directory.Exists(link);
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
}
