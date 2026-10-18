using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>M2-2（评估报告 §4.1 路线 1）：会话级状态机与运行上下文的基础语义。</summary>
public class SessionRunContextTests
{
    [Fact]
    public void State_machine_busy_states_gate_while_terminal_states_do_not()
    {
        var ctx = new SessionRunContext(7);
        Assert.Equal(SessionRunState.Idle, ctx.State);
        Assert.False(ctx.IsBusy);

        ctx.SetState(SessionRunState.Queued);
        Assert.True(ctx.IsBusy);
        ctx.SetState(SessionRunState.Running);
        Assert.True(ctx.IsBusy);
        ctx.SetState(SessionRunState.WaitingApproval);
        Assert.True(ctx.IsBusy);

        ctx.SetState(SessionRunState.Done);
        Assert.False(ctx.IsBusy);       // 终态不占闸门（可切换/可再发）
        ctx.SetState(SessionRunState.Failed);
        Assert.False(ctx.IsBusy);
        ctx.SetState(SessionRunState.Stopped);
        Assert.False(ctx.IsBusy);

        ctx.SetState(SessionRunState.Running);   // 下一轮开始
        Assert.True(ctx.IsBusy);
    }

    [Fact]
    public void State_changes_raise_event_and_skip_noop_transitions()
    {
        var ctx = new SessionRunContext(1);
        var seen = new List<SessionRunState>();
        ctx.StateChanged += c => seen.Add(c.State);

        ctx.SetState(SessionRunState.Running);
        ctx.SetState(SessionRunState.Running);   // 同值不重复触发
        ctx.SetState(SessionRunState.Done);

        Assert.Equal(new[] { SessionRunState.Running, SessionRunState.Done }, seen);
    }

    [Fact]
    public void Buffers_are_isolated_per_context()
    {
        var a = new SessionRunContext(1);
        var b = new SessionRunContext(2);

        a.PendingDelta.Append("进度A");
        a.LastFlushUtc = DateTime.UtcNow;
        b.PendingReasoning.Append("思考B");

        Assert.Empty(b.PendingDelta.ToString());
        Assert.Equal(DateTime.MinValue, b.LastFlushUtc);
        Assert.Empty(a.PendingReasoning.ToString());

        a.ResetBuffers();
        Assert.Empty(a.PendingDelta.ToString());
        Assert.Equal(DateTime.MinValue, a.LastFlushUtc);
        Assert.Equal("思考B", b.PendingReasoning.ToString());   // 清 A 不动 B
    }

    [Fact]
    public void Identity_keys_follow_persisted_or_draft_session()
    {
        var draft = new SessionRunContext(0);
        Assert.Null(draft.AuditId);
        Assert.Equal("new", draft.SessionKey);

        var saved = new SessionRunContext(42);
        Assert.Equal("agent-42", saved.AuditId);
        Assert.Equal("42", saved.SessionKey);
    }
}
