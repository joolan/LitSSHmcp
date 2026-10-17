using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class PathPolicyTests
{
    [Fact]
    public void Remote_path_under_allowed_root()
    {
        Assert.True(PathPolicy.IsRemotePathAllowed("/tmp/a.txt", new[] { "/tmp" }));
        Assert.True(PathPolicy.IsRemotePathAllowed("/var/log/app.log", new[] { "/tmp", "/var/log" }));
    }

    [Fact]
    public void Remote_path_outside_allowed_root()
    {
        Assert.False(PathPolicy.IsRemotePathAllowed("/etc/passwd", new[] { "/tmp", "/var/log" }));
        Assert.False(PathPolicy.IsRemotePathAllowed("/root/.ssh/id_rsa", new[] { "/tmp" }));
    }

    [Fact]
    public void Remote_path_traversal_is_rejected()
    {
        Assert.False(PathPolicy.IsRemotePathAllowed("/tmp/../etc/passwd", new[] { "/tmp" }));
        Assert.False(PathPolicy.IsRemotePathAllowed("../etc/passwd", new[] { "/tmp" }));
    }

    [Fact]
    public void Remote_prefix_without_separator_is_not_allowed()
    {
        Assert.False(PathPolicy.IsRemotePathAllowed("/tmpfoo/a", new[] { "/tmp" }));
    }

    [Fact]
    public void Remote_path_empty_roots_is_not_allowed()
    {
        Assert.False(PathPolicy.IsRemotePathAllowed("/tmp/a", Array.Empty<string>()));
        Assert.False(PathPolicy.IsRemotePathAllowed("/tmp/a", null));
    }

    [Fact]
    public void Local_path_under_allowed_root()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = @"C:\Users\test\Desktop";
        Assert.True(PathPolicy.IsLocalPathAllowed(@"C:\Users\test\Desktop\a.txt", new[] { root }));
        Assert.True(PathPolicy.IsLocalPathAllowed(@"c:\users\test\desktop\sub\b.txt", new[] { root }));
    }

    [Fact]
    public void Local_path_outside_allowed_root()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.False(PathPolicy.IsLocalPathAllowed(@"C:\Windows\System32\a.dll", new[] { @"C:\Users\test\Desktop" }));
    }

    [Fact]
    public void Local_path_traversal_is_normalized_and_rejected()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = @"C:\Users\test\Desktop";
        Assert.False(PathPolicy.IsLocalPathAllowed(@"C:\Users\test\Desktop\..\..\Windows\a.txt", new[] { root }));
    }

    // —— M1-5: 会话目录条件注入（评估报告 §5.3.1）——

    [Fact]
    public void WithSessionDir_injects_session_dir_under_allowed_root()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = @"C:\Users\test\Desktop";
        var session = @"C:\Users\test\Desktop\LitSSH\jobs\local\s1";

        var effective = PathPolicy.WithSessionDir(new[] { root }, session);

        Assert.Contains(session, effective);
        // 注入后会话目录及其下文件通过白名单
        Assert.True(PathPolicy.IsLocalPathAllowed(Path.Combine(session, "app.log"), effective));
        // 原有根保持不变
        Assert.Contains(root, effective);
    }

    [Fact]
    public void WithSessionDir_does_not_inject_out_of_bounds_session_dir()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // §10.C.4: 会话目录越界（桌面用户选了 C:\Windows 之类）时不注入, 否则等于把任意路径洗进白名单
        var root = @"C:\Users\test\Desktop";
        var session = @"C:\Windows\LitSSH\jobs\local\s1";

        var effective = PathPolicy.WithSessionDir(new[] { root }, session);

        Assert.DoesNotContain(session, effective);
        Assert.False(PathPolicy.IsLocalPathAllowed(Path.Combine(session, "a.txt"), effective));
    }

    [Fact]
    public void WithSessionDir_keeps_deny_all_and_null_session_semantics()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var session = @"C:\Users\test\Desktop\LitSSH\jobs\local\s1";

        // 无允许根 = deny-all，不因有会话目录而开口子
        Assert.Empty(PathPolicy.WithSessionDir(Array.Empty<string>(), session));
        Assert.Empty(PathPolicy.WithSessionDir(null, session));

        // 无会话目录 = 原列表原样
        var root = @"C:\Users\test\Desktop";
        Assert.Equal(new[] { root }, PathPolicy.WithSessionDir(new[] { root }, null));
        Assert.Equal(new[] { root }, PathPolicy.WithSessionDir(new[] { root }, "  "));
    }

    [Fact]
    public void WithSessionDir_deduplicates_when_session_equals_root()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = @"C:\Users\test\Desktop";
        var effective = PathPolicy.WithSessionDir(new[] { root }, root);

        Assert.Single(effective);
        Assert.Equal(root, effective[0]);
    }
}
