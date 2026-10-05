using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Approval;
using LitSSHmcp.Core.Services.Security;
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
