using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>审批三档范围：仅本次 / 本轮次内 / 整个会话（预检命中即自动放行）。</summary>
public class ApprovalScopeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-approvals-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Registry_session_grant_is_scoped_to_tool_target_and_command()
    {
        var registry = new ApprovalExemptionRegistry();
        Assert.True(registry.GrantSession("ssh_execute_command", "server-1", "rm -rf x", null));

        Assert.True(registry.IsApproved("ssh_execute_command", "server-1", "rm -rf x", null, null));
        Assert.True(registry.IsApproved("ssh_execute_command", "server-1", "rm -rf x", null, "r1"));

        Assert.False(registry.IsApproved("ssh_execute_command", "server-2", "rm -rf x", null, null));   // 不跨目标
        Assert.False(registry.IsApproved("ssh_execute_command", "server-1", "rm -rf /", null, null));  // 不跨命令
        Assert.False(registry.IsApproved("docker_rm", "server-1", "rm -rf x", null, null));
        Assert.False(registry.IsApproved(null, "server-1", "rm -rf x", null, null));
        Assert.False(registry.IsApproved("ssh_execute_command", null, "rm -rf x", null, null));

        Assert.True(registry.GrantSession("ssh_upload_file", "server-1", "上传 a.txt", "a.txt -> /tmp/a.txt"));
        Assert.True(registry.IsApproved("ssh_upload_file", "server-1", "上传 a.txt", "a.txt -> /tmp/a.txt", null));
        Assert.False(registry.IsApproved("ssh_upload_file", "server-1", "上传 a.txt", "a.txt -> /tmp/b.txt", null));
    }

    [Fact]
    public void Registry_round_grant_approves_only_matching_round_and_scope()
    {
        var registry = new ApprovalExemptionRegistry();
        Assert.True(registry.GrantRound("ssh_execute_command", "server-1", "rm -rf x", null, "r7"));

        Assert.True(registry.IsApproved("ssh_execute_command", "server-1", "rm -rf x", null, "r7"));
        Assert.False(registry.IsApproved("ssh_execute_command", "server-1", "rm -rf x", null, "r8"));   // 新轮次 → 恢复审批
        Assert.False(registry.IsApproved("ssh_execute_command", "server-1", "rm -rf x", null, null));   // 轮次信号丢失 → 不误放行
        Assert.False(registry.IsApproved("ssh_execute_command", "server-2", "rm -rf x", null, "r7"));
        Assert.False(registry.IsApproved("ssh_execute_command", "server-1", "rm -rf /", null, "r7"));
        Assert.False(registry.IsApproved("docker_rm", "server-1", "rm -rf x", null, "r7"));
    }

    [Fact]
    public void Registry_round_grant_requires_round_id_and_scope()
    {
        var registry = new ApprovalExemptionRegistry();

        Assert.False(registry.GrantRound("ssh_execute_command", "server-1", "rm -rf x", null, null));
        Assert.False(registry.GrantRound("ssh_execute_command", "server-1", "rm -rf x", null, "  "));
        Assert.False(registry.GrantRound(null, "server-1", "rm -rf x", null, "r1"));
        Assert.False(registry.GrantRound("ssh_execute_command", null, "rm -rf x", null, "r1"));
        Assert.False(registry.GrantRound("ssh_execute_command", "server-1", " ", null, "r1"));
        Assert.False(registry.IsApproved("ssh_execute_command", "server-1", "rm -rf x", null, "r1"));
    }

    [Fact]
    public void Registry_tool_names_are_case_insensitive()
    {
        var registry = new ApprovalExemptionRegistry();
        registry.GrantSession("SSH_Execute_Command", "server-1", "rm -rf x", null);

        Assert.True(registry.IsApproved("ssh_execute_command", "server-1", "rm -rf x", null, null));
        Assert.Equal(new[] { "SSH_Execute_Command @ server-1" }, registry.SessionGrantedTools);
    }

    [Fact]
    public void Round_tool_updates_tracker()
    {
        var tracker = new ApprovalRoundTracker();
        var tools = new ApprovalRoundTools(tracker);

        Assert.Equal("ok", tools.SetRound("r7"));
        Assert.Equal("r7", tracker.CurrentRoundId);

        tools.SetRound("   ");   // 空白视为清除（fail-safe：下一次审批恢复弹窗）
        Assert.Null(tracker.CurrentRoundId);
    }

    [Fact]
    public async Task Dispatcher_auto_approves_when_session_grant_exists()
    {
        var options = new StubSecurityOptions();
        options.Approval.Channels = new[] { "cli" };
        options.Approval.TimeoutSeconds = 15;

        var registry = new ApprovalExemptionRegistry();
        registry.GrantSession("ssh_execute_command", "server-1", "rm -rf x", null);

        var service = new ApprovalService(options, new DesktopApprovalService(options), new CliApprovalChannel(_dir),
            roundTracker: new ApprovalRoundTracker(), exemptions: registry);

        AuditContext.CurrentTool = "ssh_execute_command";
        try
        {
            // 若预检未命中会去等 CLI 通道（无人决策→超时），这里应立即返回 Approved
            Assert.Equal(ApprovalOutcome.Approved,
                await service.RequestApprovalAsync("server-1", "rm -rf x", CommandFilterResult.Sensitive));
        }
        finally
        {
            AuditContext.CurrentTool = null;
        }
    }

    [Fact]
    public async Task Dispatcher_auto_approves_when_round_grant_matches_current_round()
    {
        var options = new StubSecurityOptions();
        options.Approval.Channels = new[] { "cli" };
        options.Approval.TimeoutSeconds = 15;

        var tracker = new ApprovalRoundTracker { CurrentRoundId = "r7" };
        var registry = new ApprovalExemptionRegistry();
        registry.GrantRound("ssh_execute_command", "server-1", "rm -rf x", null, "r7");

        var service = new ApprovalService(options, new DesktopApprovalService(options, roundTracker: tracker, exemptions: registry),
            new CliApprovalChannel(_dir), roundTracker: tracker, exemptions: registry);

        AuditContext.CurrentTool = "ssh_execute_command";
        try
        {
            Assert.Equal(ApprovalOutcome.Approved,
                await service.RequestApprovalAsync("server-1", "rm -rf x", CommandFilterResult.Sensitive));
        }
        finally
        {
            AuditContext.CurrentTool = null;
        }
    }

    [Fact]
    public async Task Dispatcher_prompts_again_when_round_mismatch()
    {
        var options = new StubSecurityOptions();
        options.Approval.Channels = new[] { "cli" };
        options.Approval.TimeoutSeconds = 1;

        var tracker = new ApprovalRoundTracker { CurrentRoundId = "r8" };
        var registry = new ApprovalExemptionRegistry();
        registry.GrantRound("ssh_execute_command", "server-1", "rm -rf x", null, "r6");   // 上一轮的授权

        var service = new ApprovalService(options, new DesktopApprovalService(options, roundTracker: tracker, exemptions: registry),
            new CliApprovalChannel(_dir), roundTracker: tracker, exemptions: registry);

        AuditContext.CurrentTool = "ssh_execute_command";
        try
        {
            // 轮次已推进 → 预检不命中 → 走通道 → 无人决策 → 超时（证明恢复了审批）
            Assert.Equal(ApprovalOutcome.Timeout,
                await service.RequestApprovalAsync("server-1", "rm -rf x", CommandFilterResult.Sensitive));
        }
        finally
        {
            AuditContext.CurrentTool = null;
        }
    }

    [Fact]
    public async Task Dispatcher_auto_reject_mode_wins_over_grant()
    {
        var options = new StubSecurityOptions();
        options.Approval.Mode = "auto-reject";
        options.Approval.Channels = new[] { "cli" };

        var registry = new ApprovalExemptionRegistry();
        registry.GrantSession("ssh_execute_command", "server-1", "rm -rf x", null);

        var service = new ApprovalService(options, new DesktopApprovalService(options), new CliApprovalChannel(_dir),
            roundTracker: new ApprovalRoundTracker(), exemptions: registry);

        AuditContext.CurrentTool = "ssh_execute_command";
        try
        {
            Assert.Equal(ApprovalOutcome.AutoRejected,
                await service.RequestApprovalAsync("server-1", "rm -rf x", CommandFilterResult.Sensitive));
        }
        finally
        {
            AuditContext.CurrentTool = null;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    private sealed class StubSecurityOptions : ISecurityOptionsProvider
    {
        public bool Enabled { get; set; } = true;
        public CommandFilterConfig CommandFilter { get; set; } = new();
        public SqlFilterConfig SqlFilter { get; set; } = new();
        public ScriptRiskConfig ScriptRisk { get; set; } = new();
        public FileTransferConfig FileTransfer { get; set; } = new();
        public SshHostKeyConfig SshHostKey { get; set; } = new();
        public DiscoveryConfig Discovery { get; set; } = new();
        public LimitsConfig Limits { get; set; } = new();
        public AuditConfig Audit { get; set; } = new();
        public ApprovalConfig Approval { get; set; } = new();
        public ConnectionPoolConfig ConnectionPool { get; set; } = new();
        public void Invalidate() { }
    }
}
