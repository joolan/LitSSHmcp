using Microsoft.Extensions.AI;
using LitSSHmcp.Agent.Sessions;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// 长任务轮末压缩：动态上下文（工作记忆/检索/子代理摘要）虽用 User 角色注入，
/// 但绝不能被当成真实用户轮边界，否则本轮刚取回的工具结果会被误压。
/// </summary>
public class ContextBoundaryTests
{
    [Fact]
    public void Last_user_turn_ignores_dynamic_context_appended_after_real_user()
    {
        var realUser = new ChatMessage(ChatRole.User, "排查 nginx 502");
        var toolResult = new ChatMessage(ChatRole.Tool, "error log");
        var workingMemory = new ChatMessage(ChatRole.User, "## 工作记忆\n...");
        var retrieval = new ChatMessage(ChatRole.User, "## 检索召回\n...");

        var messages = new List<ChatMessage> { realUser, toolResult, workingMemory, retrieval };
        var dynamicContext = new List<ChatMessage> { workingMemory, retrieval };

        var index = AgentSession.FindLastUserTurnIndex(messages, dynamicContext);

        Assert.Equal(0, index); // 必须定位到真实用户轮，而不是末尾的动态上下文
    }

    [Fact]
    public void Last_user_turn_without_dynamic_context_uses_trailing_user()
    {
        var first = new ChatMessage(ChatRole.User, "第一轮");
        var last = new ChatMessage(ChatRole.User, "第二轮");

        var index = AgentSession.FindLastUserTurnIndex(
            new List<ChatMessage> { first, new ChatMessage(ChatRole.Assistant, "ok"), last },
            Array.Empty<ChatMessage>());

        Assert.Equal(2, index);
    }

    [Fact]
    public void Last_user_turn_returns_minus_one_when_only_dynamic_context_exists()
    {
        var dynamicOnly = new ChatMessage(ChatRole.User, "仅动态上下文");

        var index = AgentSession.FindLastUserTurnIndex(
            new List<ChatMessage> { new ChatMessage(ChatRole.System, "s"), dynamicOnly },
            new List<ChatMessage> { dynamicOnly });

        Assert.Equal(-1, index);
    }
}
