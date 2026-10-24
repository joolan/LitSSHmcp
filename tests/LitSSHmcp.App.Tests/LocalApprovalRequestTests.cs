using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class LocalApprovalRequestTests
{
    [Fact]
    public async Task Resolve_returns_decision_and_updates_card_state()
    {
        var request = new LocalApprovalRequest("git status", "/repo", "agent-1", sessionAllGranted: false);
        var turn = new AgentTurn("检查仓库", DateTime.UtcNow);

        turn.Approvals.Add(request);
        Assert.Single(turn.Approvals);
        Assert.True(request.IsPending);

        request.Resolve(LocalApprovalDecision.Allow);

        Assert.False(request.IsPending);
        Assert.Equal(LocalApprovalDecision.Allow, await request.DecisionTask);
        Assert.Equal("已允许一次", request.ResolutionText);
        Assert.Equal(System.Windows.Visibility.Visible, request.ResolvedVisibility);
    }

    [Fact]
    public async Task Resolve_is_idempotent_and_first_decision_wins()
    {
        var request = new LocalApprovalRequest("rm -rf build", "/repo", null, sessionAllGranted: false);

        request.Resolve(LocalApprovalDecision.Deny);
        request.Resolve(LocalApprovalDecision.AlwaysAllow);

        Assert.Equal(LocalApprovalDecision.Deny, await request.DecisionTask);
        Assert.Equal("已拒绝", request.ResolutionText);
    }
}
