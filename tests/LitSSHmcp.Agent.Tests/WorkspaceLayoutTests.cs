using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>两层工作区布局（评估报告需求 D / D6）：jobs/&lt;owner&gt;/s&lt;sessionId&gt; + 目录名段消毒。</summary>
public class WorkspaceLayoutTests
{
    [Fact]
    public void ResolveSessionDir_builds_jobs_tree_under_shared_root()
    {
        var shared = Path.Combine(Path.GetTempPath(), "litssh-shared-root");
        var dir = WorkspaceLayout.ResolveSessionDir(shared, "local", 42);

        Assert.Equal(Path.GetFullPath(Path.Combine(shared, "jobs", "local", "s42")), dir);
        Assert.StartsWith(Path.GetFullPath(shared), dir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveSessionDir_sanitizes_owner_segment()
    {
        var shared = Path.Combine(Path.GetTempPath(), "litssh-shared-root");

        // 路径分隔符与非法字符被消毒，不能借 owner 逃出 jobs/ 树
        var evil = WorkspaceLayout.ResolveSessionDir(shared, "../..", 7);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(shared, "jobs")) + Path.DirectorySeparatorChar,
            evil, StringComparison.OrdinalIgnoreCase);

        var weird = WorkspaceLayout.ResolveSessionDir(shared, "a/b\\c:d*e?", 7);
        Assert.Equal(WorkspaceLayout.SanitizeSegment("a/b\\c:d*e?", "local"), Path.GetFileName(Path.GetDirectoryName(weird)));
        Assert.DoesNotContain('/', Path.GetFileName(Path.GetDirectoryName(weird)!));
        Assert.DoesNotContain('*', Path.GetFileName(Path.GetDirectoryName(weird)!));

        // 空/纯点回退 local（桌面未登录归因，D1）
        Assert.Equal("local", WorkspaceLayout.SanitizeSegment(null, "local"));
        Assert.Equal("local", WorkspaceLayout.SanitizeSegment("   ", "local"));
        Assert.Equal("local", WorkspaceLayout.SanitizeSegment("..", "local"));
        Assert.Equal("local", WorkspaceLayout.SanitizeSegment(".", "local"));
    }

    [Fact]
    public void SanitizeSegment_caps_length_and_keeps_normal_names()
    {
        Assert.Equal("web-user-2", WorkspaceLayout.SanitizeSegment("web-user-2", "local"));
        Assert.Equal(64, WorkspaceLayout.SanitizeSegment(new string('x', 200), "local").Length);
    }

    [Fact]
    public void Session_segment_is_prefixed()
    {
        Assert.Equal("s99", WorkspaceLayout.SessionSegment(99));
    }
}
