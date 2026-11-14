using System.Text.Json;
using LitSSHmcp.McpServer.Services;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class JsonOutputTests
{
    [Fact]
    public void User_visible_json_keeps_chinese_and_time_offset_readable()
    {
        var json = JsonSerializer.Serialize(new
        {
            server = "虚拟机服务器",
            createdAt = "2026-11-14T03:07:49+00:00"
        }, JsonOutput.Options);

        Assert.Contains("虚拟机服务器", json);
        Assert.Contains("+00:00", json);
        Assert.DoesNotContain("\\u865A", json);
        Assert.DoesNotContain("\\u002B", json);
    }
}
