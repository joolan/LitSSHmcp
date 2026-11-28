using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>AgentEvent 错误分类 + token 字段的可观测性回归。</summary>
public class AgentEventObservabilityTests
{
    [Fact]
    public void Error_kind_and_token_fields_default_to_null_for_legacy_events()
    {
        // 老调用点只用位置参数/既有命名参数，新增字段必须可选，不能破坏源码兼容。
        var e = new AgentEvent(AgentEventKind.AssistantText, "hello");

        Assert.Null(e.ErrorKind);
        Assert.Null(e.InputTokens);
        Assert.Null(e.OutputTokens);
        Assert.Equal("hello", e.Text);
    }

    [Theory]
    [InlineData(AgentErrorKind.Budget, "成本/预算超限")]
    [InlineData(AgentErrorKind.Guardrail, "harness 护栏停止")]
    [InlineData(AgentErrorKind.ContentFilter, "模型输出被内容过滤")]
    public void New_harness_error_kinds_have_actionable_descriptions(AgentErrorKind kind, string expected)
    {
        Assert.Contains(expected, ChatErrorClassifier.Describe(kind));
        // Unknown 仍保留异常摘要，UI 连接失败提示不受重构影响。
        Assert.Contains("boom", ChatErrorClassifier.Describe(new InvalidOperationException("boom")));
    }

    [Fact]
    public void Error_kind_enum_values_are_stable()
    {
        Assert.Equal(7, (int)AgentErrorKind.Unknown);
        Assert.Equal(8, (int)AgentErrorKind.Budget);
        Assert.Equal(9, (int)AgentErrorKind.Guardrail);
        Assert.Equal(10, (int)AgentErrorKind.ContentFilter);
    }
}
