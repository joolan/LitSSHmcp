using LitSSHmcp.Agent.Sessions;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// 滚动摘要输入：长历史必须双端采样——头部保留目标/早期探查，尾部保留最新结论与报错。
/// 旧实现从最旧消息 append 到 8000 字符即 break，错误结论在尾部被截掉。
/// </summary>
public class SummaryTranscriptTests
{
    [Fact]
    public void Short_transcript_is_returned_verbatim()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "排查 nginx 502"),
            new ChatMessage(ChatRole.Tool, "error: upstream timeout")
        };

        var transcript = AgentSession.BuildTranscript(messages);

        Assert.Contains("排查 nginx 502", transcript);
        Assert.Contains("error: upstream timeout", transcript);
        Assert.DoesNotContain("已省略", transcript);
    }

    [Fact]
    public void Long_transcript_keeps_head_goal_and_tail_error()
    {
        // 构造远超 8000 字符的历史：头部是目标+早期探查，尾部是错误结论。
        var messages = new List<ChatMessage> { new(ChatRole.User, "TARGET: 排查支付服务 502") };
        for (var i = 0; i < 30; i++)
            messages.Add(new ChatMessage(ChatRole.Tool, $"早期探查 {i}: " + new string('x', 400)));
        messages.Add(new ChatMessage(ChatRole.Assistant,
            "ROOT-CAUSE: 上游支付服务连接池耗尽，需扩容 max_connections"));

        var transcript = AgentSession.BuildTranscript(messages);

        Assert.Contains("TARGET: 排查支付服务 502", transcript);               // 头部骨架
        Assert.Contains("ROOT-CAUSE: 上游支付服务连接池耗尽", transcript);       // 尾部结论
        Assert.Contains("已省略", transcript);                                  // 显式标注省略
        Assert.True(transcript.Length <= 8400, $"transcript 未受预算约束: {transcript.Length}");
    }

    [Fact]
    public void Single_huge_message_keeps_its_tail_error()
    {
        var text = new string('a', 12000) + "FATAL: disk full";
        var messages = new[] { new ChatMessage(ChatRole.Tool, text) };

        var transcript = AgentSession.BuildTranscript(messages);

        Assert.Contains("FATAL: disk full", transcript);
        Assert.Contains("本条中段省略", transcript);
        Assert.True(transcript.Length < 2500, $"单条消息未被限长: {transcript.Length}");
    }
}
