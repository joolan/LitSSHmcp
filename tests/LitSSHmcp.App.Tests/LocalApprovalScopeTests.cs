using System.Threading;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>本机终端审批三档范围（全部命令级）：仅本次 / 本轮次内全部命令 / 本次会话全部命令。</summary>
[Collection("local-audit")]   // OverrideSink 是进程级静态替身: 本类也经 LocalTerminalTool 写审计, 须与 PromoteToolTests 等串行防相互覆盖
public class LocalApprovalScopeTests
{
    [Fact]
    public void Registry_session_all_approves_any_round_within_same_session_only()
    {
        var registry = new LocalApprovalExemptionRegistry();
        registry.GrantSessionAll("1");

        Assert.True(registry.IsApproved(null, "1"));
        Assert.True(registry.IsApproved("r1", "1"));
        Assert.True(registry.SessionAllGranted("1"));

        // §4.1 并行：A 会话的「本次会话允许全部」不放行 B 会话
        Assert.False(registry.IsApproved(null, "2"));
        Assert.False(registry.IsApproved("r1", "2"));
        Assert.False(registry.SessionAllGranted("2"));
        Assert.False(registry.SessionAllGranted(null));
    }

    [Fact]
    public void Registry_session_all_requires_session_key()
    {
        var registry = new LocalApprovalExemptionRegistry();
        registry.GrantSessionAll(null);    // fail-safe：空键不登记
        registry.GrantSessionAll("   ");

        Assert.False(registry.IsApproved("r1", null));
        Assert.False(registry.IsApproved("r1", "1"));
    }

    [Fact]
    public void Registry_round_all_approves_only_matching_round()
    {
        var registry = new LocalApprovalExemptionRegistry();
        Assert.True(registry.GrantRoundAll("r7"));

        Assert.True(registry.IsApproved("r7", null));
        Assert.True(registry.IsApproved("r7", "2"));   // 轮次授权跨会话也只认轮次 ID（ID 唯一 + AsyncLocal）
        Assert.False(registry.IsApproved("r8", null));   // 下一条消息 → 恢复审批
        Assert.False(registry.IsApproved(null, null));   // 轮次信号丢失 → 只多弹窗
        Assert.False(registry.SessionAllGranted("1"));
    }

    [Fact]
    public void Registry_round_all_requires_round_id()
    {
        var registry = new LocalApprovalExemptionRegistry();

        Assert.False(registry.GrantRoundAll(null));
        Assert.False(registry.GrantRoundAll("  "));
        Assert.False(registry.IsApproved("r1", null));
    }

    [Fact]
    public async Task Tool_skips_approval_for_any_command_when_session_all_granted()
    {
        var terminal = new LocalTerminalSessionViewModel();
        await terminal.EnsureStartedAsync(80, 24);
        try
        {
            var cfg = new LocalTerminalConfig { AllowedCommands = new[] { "Write-Output" }, TimeoutSeconds = 60 };
            var registry = new LocalApprovalExemptionRegistry();
            var approvalCalls = 0;
            var tool = new LocalTerminalTool(() => "test", _ => terminal, cfg,
                (_, _, _) => { approvalCalls++; return Task.FromResult(LocalApprovalDecision.Deny); }, () => { }, registry);

            registry.GrantSessionAll("test");
            await tool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "Write-Output LITSSH_ALL_1" }, CancellationToken.None);
            await tool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "Write-Output LITSSH_ALL_2" }, CancellationToken.None);

            Assert.Equal(0, approvalCalls);   // 任意命令均不再弹窗
        }
        finally
        {
            await terminal.DisposeAsync();
        }
    }

    [Fact]
    public async Task Tool_prompts_again_when_session_grant_belongs_to_other_session()
    {
        // §4.1：另一会话授予的「本次会话允许全部」不能免掉本会话的审批
        var terminal = new LocalTerminalSessionViewModel();
        await terminal.EnsureStartedAsync(80, 24);
        try
        {
            var cfg = new LocalTerminalConfig { AllowedCommands = new[] { "Write-Output" }, TimeoutSeconds = 60 };
            var registry = new LocalApprovalExemptionRegistry();
            var approvalCalls = 0;
            var key = "test";
            var tool = new LocalTerminalTool(() => key, _ => terminal, cfg,
                (_, _, _) => { approvalCalls++; return Task.FromResult(LocalApprovalDecision.Deny); }, () => { }, registry);

            registry.GrantSessionAll("other");   // 授权属于别的会话
            var result = await tool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "Write-Output LITSSH_XS" }, CancellationToken.None);

            Assert.Equal(1, approvalCalls);
            Assert.Contains("拒绝", result?.ToString());
        }
        finally
        {
            await terminal.DisposeAsync();
        }
    }

    [Fact]
    public async Task Tool_skips_approval_when_round_all_matches_current_round()
    {
        var terminal = new LocalTerminalSessionViewModel();
        await terminal.EnsureStartedAsync(80, 24);
        try
        {
            var cfg = new LocalTerminalConfig { AllowedCommands = new[] { "Write-Output" }, TimeoutSeconds = 60 };
            var registry = new LocalApprovalExemptionRegistry();
            var approvalCalls = 0;
            var tool = new LocalTerminalTool(() => "test", _ => terminal, cfg,
                (_, _, _) => { approvalCalls++; return Task.FromResult(LocalApprovalDecision.Deny); }, () => { }, registry);

            ApprovalRoundContext.CurrentRoundId = "r7";
            registry.GrantRoundAll("r7");
            await tool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "Write-Output LITSSH_R7" }, CancellationToken.None);
            await tool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "Write-Output LITSSH_R7_2" }, CancellationToken.None);

            Assert.Equal(0, approvalCalls);
        }
        finally
        {
            ApprovalRoundContext.CurrentRoundId = null;
            await terminal.DisposeAsync();
        }
    }

    [Fact]
    public async Task Tool_prompts_again_when_round_mismatch()
    {
        var terminal = new LocalTerminalSessionViewModel();
        await terminal.EnsureStartedAsync(80, 24);
        try
        {
            var cfg = new LocalTerminalConfig { AllowedCommands = new[] { "Write-Output" }, TimeoutSeconds = 60 };
            var registry = new LocalApprovalExemptionRegistry();
            var approvalCalls = 0;
            var tool = new LocalTerminalTool(() => "test", _ => terminal, cfg,
                (_, _, _) => { approvalCalls++; return Task.FromResult(LocalApprovalDecision.Deny); }, () => { }, registry);

            ApprovalRoundContext.CurrentRoundId = "r8";   // 新一轮
            registry.GrantRoundAll("r6");   // 上一轮的授权
            var result = await tool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "Write-Output LITSSH_OLD" }, CancellationToken.None);

            Assert.Equal(1, approvalCalls);   // 轮次已推进 → 恢复审批
            Assert.Contains("拒绝", result?.ToString());
        }
        finally
        {
            ApprovalRoundContext.CurrentRoundId = null;
            await terminal.DisposeAsync();
        }
    }

    [Fact]
    public async Task Tool_AllowRound_decision_grants_all_commands_for_current_round()
    {
        var terminal = new LocalTerminalSessionViewModel();
        await terminal.EnsureStartedAsync(80, 24);
        try
        {
            var cfg = new LocalTerminalConfig { AllowedCommands = new[] { "Write-Output" }, TimeoutSeconds = 60 };
            var registry = new LocalApprovalExemptionRegistry();
            var approvalCalls = 0;
            var tool = new LocalTerminalTool(() => "test", _ => terminal, cfg,
                (_, _, _) => { approvalCalls++; return Task.FromResult(LocalApprovalDecision.AllowRound); }, () => { }, registry);

            ApprovalRoundContext.CurrentRoundId = "r7";
            await tool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "Write-Output LITSSH_AR_1" }, CancellationToken.None);
            await tool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "Write-Output LITSSH_AR_2" }, CancellationToken.None);
            Assert.Equal(1, approvalCalls);   // 同轮次内后续任意命令不再弹窗

            ApprovalRoundContext.CurrentRoundId = "r8";   // 换一轮 → 恢复审批
            await tool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "Write-Output LITSSH_AR_3" }, CancellationToken.None);
            Assert.Equal(2, approvalCalls);
        }
        finally
        {
            ApprovalRoundContext.CurrentRoundId = null;
            await terminal.DisposeAsync();
        }
    }
}
