using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>批量下载相对路径计算：必须相对顶层下载根目录、保留子目录层级（回归"子目录被拍平"）。</summary>
public class SshRelativizeTests
{
    [Theory]
    [InlineData("/tmp/dl", "/tmp/dl/sub/nested.txt", "nested.txt", "sub/nested.txt")]
    [InlineData("/tmp/dl", "/tmp/dl/sub/dir/x.md", "x.md", "sub/dir/x.md")]
    [InlineData("/tmp/dl", "/tmp/dl/top.txt", "top.txt", "top.txt")]
    [InlineData("/tmp/dl/", "/tmp/dl/sub/x", "x", "sub/x")]
    [InlineData(null, "/tmp/dl/top.txt", "top.txt", "top.txt")]
    [InlineData("/tmp/dl", "/other/x.txt", "fallback.txt", "fallback.txt")]
    public void Relativize_keeps_subdirectory_structure(string? root, string full, string fallback, string expected)
    {
        Assert.Equal(expected, SshService.Relativize(root, full, fallback));
    }
}
