using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ChatErrorClassifierTests
{
    [Fact]
    public void Classify_maps_common_errors()
    {
        Assert.Equal(AgentErrorKind.Network, ChatErrorClassifier.Classify(new HttpRequestException("连接失败")));
        Assert.Equal(AgentErrorKind.Cancelled, ChatErrorClassifier.Classify(new OperationCanceledException()));
        Assert.Equal(AgentErrorKind.Unknown, ChatErrorClassifier.Classify(new InvalidOperationException("x")));
    }

    [Fact]
    public void IsTransient_only_for_retryable()
    {
        Assert.True(ChatErrorClassifier.IsTransient(new HttpRequestException()));
        Assert.False(ChatErrorClassifier.IsTransient(new OperationCanceledException()));
        Assert.False(ChatErrorClassifier.IsTransient(new InvalidOperationException()));
    }
}
