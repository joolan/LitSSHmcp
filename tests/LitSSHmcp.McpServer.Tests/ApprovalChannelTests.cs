using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Approval;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class ApprovalChannelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-approvals-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Cli_channel_approves_when_decision_written()
    {
        var channel = new CliApprovalChannel(_dir);
        var context = new ApprovalRequestContext { ServerName = "s", Operation = "op", Command = "rm x", TimeoutSeconds = 15 };

        var task = channel.RequestAsync(context, CancellationToken.None);

        var id = await WaitForPendingIdAsync();
        ApprovalFileStore.WriteDecision(_dir, new ApprovalDecisionFile { Id = id, Approved = true, DecidedAt = DateTimeOffset.Now, Channel = "cli" });

        Assert.Equal(ApprovalOutcome.Approved, await task);
    }

    [Fact]
    public async Task Cli_channel_reports_timeout_when_no_decision()
    {
        var channel = new CliApprovalChannel(_dir);
        var context = new ApprovalRequestContext { ServerName = "s", Operation = "op", Command = "rm x", TimeoutSeconds = 1 };

        Assert.Equal(ApprovalOutcome.Timeout, await channel.RequestAsync(context, CancellationToken.None));
    }

    /// <summary>
    /// M3-5：pending 文件携带宿主注入的归因 Owner（AuditContext ← LITSSH_OWNER_USER_ID），
    /// Web 宿主凭它做浏览器推送的身份过滤（报告 §3.3.1/§10.B.5）。
    /// </summary>
    [Fact]
    public async Task Cli_channel_pending_carries_audit_owner()
    {
        AuditContext.CurrentUserId = "42";
        try
        {
            var channel = new CliApprovalChannel(_dir);
            var task = channel.RequestAsync(
                new ApprovalRequestContext { ServerName = "s", Operation = "op", Command = "ls", TimeoutSeconds = 15 },
                CancellationToken.None);

            var id = await WaitForPendingIdAsync();
            var pending = ApprovalFileStore.ListPending(_dir).Single(p => p.Id == id);
            Assert.Equal("42", pending.Owner);

            ApprovalFileStore.WriteDecision(_dir, new ApprovalDecisionFile { Id = id, Approved = true, DecidedAt = DateTimeOffset.Now, Channel = "cli" });
            Assert.Equal(ApprovalOutcome.Approved, await task);
        }
        finally
        {
            AuditContext.CurrentUserId = null;
        }
    }

    [Fact]
    public async Task Dispatcher_uses_cli_channel_and_honors_decision()
    {
        var options = new StubSecurityOptions();
        options.Approval.Channels = new[] { "cli" };
        options.Approval.TimeoutSeconds = 15;

        var service = new ApprovalService(options, new DesktopApprovalService(options), new CliApprovalChannel(_dir));

        var task = service.RequestApprovalAsync("server-1", "rm -rf x", CommandFilterResult.Sensitive);

        var id = await WaitForPendingIdAsync();
        ApprovalFileStore.WriteDecision(_dir, new ApprovalDecisionFile { Id = id, Approved = false, DecidedAt = DateTimeOffset.Now, Channel = "cli" });

        Assert.Equal(ApprovalOutcome.Rejected, await task);
    }

    [Fact]
    public async Task Dispatcher_auto_approves_when_mode_enabled_without_waiting_channels()
    {
        var options = new StubSecurityOptions();
        options.Approval.Mode = "auto-approve";
        options.Approval.Channels = new[] { "cli" };
        options.Approval.TimeoutSeconds = 30;

        var service = new ApprovalService(options, new DesktopApprovalService(options), new CliApprovalChannel(_dir));

        // 若未短路就会去等 CLI 通道（无人决策→超时），这里应立即返回 Approved
        var outcome = await service.RequestApprovalAsync("server-1", "rm -rf x", CommandFilterResult.Sensitive);

        Assert.Equal(ApprovalOutcome.Approved, outcome);
    }

    [Fact]
    public async Task Dispatcher_auto_rejects_when_mode_enabled()
    {
        var options = new StubSecurityOptions();
        options.Approval.Mode = "auto-reject";
        options.Approval.Channels = new[] { "cli" };

        var service = new ApprovalService(options, new DesktopApprovalService(options), new CliApprovalChannel(_dir));

        var outcome = await service.RequestApprovalAsync("server-1", "rm -rf x", CommandFilterResult.Sensitive);

        Assert.Equal(ApprovalOutcome.AutoRejected, outcome);
    }

    [Theory]
    [InlineData("manual", ApprovalMode.Manual)]
    [InlineData("auto-approve", ApprovalMode.AutoApprove)]
    [InlineData("auto_approve", ApprovalMode.AutoApprove)]
    [InlineData("auto-reject", ApprovalMode.AutoReject)]
    [InlineData("REJECT", ApprovalMode.AutoReject)]
    [InlineData("bogus", ApprovalMode.Manual)]
    [InlineData("", ApprovalMode.Manual)]
    public void ApprovalModeParser_parses_and_defaults_to_manual(string input, ApprovalMode expected)
        => Assert.Equal(expected, ApprovalModeParser.Parse(input));

    /// <summary>
    /// 超时钟在「弹窗可见」时重置：桌面弹窗一次只显示一个，排队中的请求若沿用发起时刻会误判超时。
    /// </summary>
    [Fact]
    public async Task Timeout_clock_extends_when_reset_before_expiry()
    {
        using var clock = new ApprovalTimeoutClock(TimeSpan.FromMilliseconds(400));

        // 300ms 时重置（等价于弹窗刚可见），再等 300ms（累计 600ms，但新窗口才过 300ms）不应超时
        await Task.Delay(300);
        clock.Reset();
        await Task.Delay(300);
        Assert.False(clock.Expired.IsCompleted, "重置后的窗口未到不应超时");

        // 再等 300ms（新窗口累计 600ms > 400ms）应超时
        await Task.Delay(300);
        await clock.Expired;   // 窗口届满 → 正常完成
    }

    [Fact]
    public async Task Timeout_clock_expires_without_reset()
    {
        using var clock = new ApprovalTimeoutClock(TimeSpan.FromMilliseconds(150));

        await clock.Expired;   // 窗口届满 → 正常完成（非取消）
        Assert.True(clock.Expired.IsCompletedSuccessfully);
    }

    private async Task<string> WaitForPendingIdAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var pending = ApprovalFileStore.ListPending(_dir);
            if (pending.Count > 0)
                return pending[0].Id;

            await Task.Delay(100);
        }

        throw new TimeoutException("未出现待审批请求文件");
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
