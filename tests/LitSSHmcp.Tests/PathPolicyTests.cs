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
}
