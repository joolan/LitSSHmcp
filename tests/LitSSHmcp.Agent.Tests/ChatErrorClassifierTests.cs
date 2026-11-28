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

    [Fact]
    public void Timeout_exception_classifies_as_timeout_and_transient()
    {
        // ResilientChatClient 把内部超时 CTS 的 OCE 包装为 TimeoutException：
        // 必须归类为 Timeout（可重试、提示正确），而不是与用户取消混淆。
        var ex = new TimeoutException("模型请求超时", new OperationCanceledException());
        Assert.Equal(AgentErrorKind.Timeout, ChatErrorClassifier.Classify(ex));
        Assert.True(ChatErrorClassifier.IsTransient(ex));
        Assert.Contains("超时", ChatErrorClassifier.Describe(ex));
    }
}
