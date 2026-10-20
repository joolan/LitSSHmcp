using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class SshProbeFormatterTests
{
    [Fact]
    public void Success_reports_connected()
    {
        Assert.Equal("连接成功", SshProbeFormatter.Describe(new ConnectionProbeResult { Success = true }));
    }

    [Theory]
    [InlineData("auth", "认证失败")]
    [InlineData("host_key", "主机密钥不匹配")]
    [InlineData("timeout", "连接超时")]
    [InlineData("network", "网络不可达")]
    public void Failure_maps_error_kind_to_readable_reason(string kind, string expected)
    {
        var text = SshProbeFormatter.Describe(new ConnectionProbeResult
        {
            Success = false,
            ErrorKind = kind,
            Error = "raw detail"
        });

        Assert.Contains(expected, text);
        Assert.Contains("raw detail", text);
    }

    [Fact]
    public void Unknown_kind_without_detail_falls_back_to_generic_message()
    {
        Assert.Equal("连接失败：连接失败", SshProbeFormatter.Describe(new ConnectionProbeResult
        {
            Success = false,
            ErrorKind = "unknown",
            Error = string.Empty
        }));
    }
}
