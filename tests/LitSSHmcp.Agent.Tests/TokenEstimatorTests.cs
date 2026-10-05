using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class TokenEstimatorTests
{
    [Theory]
    [InlineData("你好吗", 3)]      // 3 个 CJK 字符
    [InlineData("abcd", 1)]       // 4 个 ASCII → 1
    [InlineData("", 0)]
    public void EstimateText_handles_cjk_and_ascii(string text, int expected)
    {
        Assert.Equal(expected, TokenEstimator.EstimateText(text));
    }

    [Fact]
    public void Estimate_messages_sums_content()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "你好"),        // 2
            new(ChatRole.Assistant, "abcd")    // 1
        };
        Assert.Equal(3, TokenEstimator.Estimate(messages));
    }
}
