using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ToolProfileSuggesterTests
{
    [Theory]
    [InlineData("帮我重启 nginx", AgentToolProfile.Change)]
    [InlineData("发布新版本到 web01", AgentToolProfile.Change)]
    [InlineData("磁盘满了怎么排查", AgentToolProfile.Diagnose)]
    [InlineData("服务报错 timeout", AgentToolProfile.Diagnose)]
    [InlineData("这个应用的拓扑依赖和影响面", AgentToolProfile.Assets)]
    public void Suggests_expected_profile(string text, AgentToolProfile expected) =>
        Assert.Equal(expected, ToolProfileSuggester.Suggest(text));

    [Fact]
    public void Returns_null_for_generic_chat() =>
        Assert.Null(ToolProfileSuggester.Suggest("你好，介绍一下你自己"));
}
