using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class RemotePathGuardTests
{
    [Theory]
    [InlineData("..\\..\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\Startup\\evil.bat")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\evil.exe")]
    [InlineData("C:evil.txt")]
    [InlineData("../evil.txt")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("evil.txt:stream")]
    [InlineData("evil\n.txt")]
    [InlineData("evil\0.txt")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("COM1.log")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("a/b.txt")]
    public void TryFileName_rejects_unsafe_names(string name)
        => Assert.False(RemotePathGuard.TryFileName(name, out _));

    [Theory]
    [InlineData("app.log")]
    [InlineData("数据 文件.txt")]
    [InlineData("a-b_c.2026.tar.gz")]
    [InlineData(".hidden")]
    public void TryFileName_accepts_single_segment_names(string name)
    {
        Assert.True(RemotePathGuard.TryFileName(name, out var safe));
        Assert.Equal(name, safe);
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("../evil")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/evil")]
    [InlineData("a/../evil")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("a\\b")]
    [InlineData("a/b:c")]
    public void TryRelativePath_rejects_unsafe_paths(string path)
        => Assert.False(RemotePathGuard.TryRelativePath(path, out _));

    [Theory]
    [InlineData("a/b.txt", "a/b.txt")]
    [InlineData("日志/2026-10-21.log", "日志/2026-10-21.log")]
    [InlineData("a-b/c_d.conf", "a-b/c_d.conf")]
    public void TryRelativePath_accepts_safe_paths(string path, string expected)
    {
        Assert.True(RemotePathGuard.TryRelativePath(path, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void ResolveLocalUnder_returns_path_inside_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "litssh-pathguard-tests");
        var full = RemotePathGuard.ResolveLocalUnder(root, "a/b.txt");

        Assert.NotNull(full);
        Assert.True(PathGuard.IsUnder(root, full));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("C:\\Windows\\evil.txt")]
    public void ResolveLocalUnder_rejects_escapes(string relative)
        => Assert.Null(RemotePathGuard.ResolveLocalUnder(Path.GetTempPath(), relative));

    [Theory]
    [InlineData("evil.bat", true)]
    [InlineData("setup.EXE", true)]
    [InlineData("payload.ps1", true)]
    [InlineData("notes.txt", false)]
    [InlineData("archive.tar.gz", false)]
    public void IsPotentiallyExecutableFileName_detects_script_and_binary_types(string name, bool expected)
        => Assert.Equal(expected, RemotePathGuard.IsPotentiallyExecutableFileName(name));
}
