using System.Threading;
using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

[Collection("local-audit")]   // OverrideSink 是进程级静态替身: 与 PromoteToolTests 等审计测试类串行, 防相互覆盖
public class LocalTerminalToolTests
{
    /// <summary>
    /// M1-6（评估报告待办 #8 / §10.A.1）：本机终端此前零审计——核查确认后随 M1 补入：
    /// 策略拦截、用户拒绝、实际执行三类事件都要进审计链（Local 类别 + 会话归因）。
    /// </summary>
    [Fact]
    public async Task Audits_policy_block_user_deny_and_execution()
    {
        var records = new List<CommandAuditLog>();
        var marker = Guid.NewGuid().ToString("N")[..8];
        LocalAudit.OverrideSink = log => { lock (records) records.Add(log); return Task.CompletedTask; };
        LocalAudit.AmbientSessionId = "agent-audit-test";
        try
        {
            // ① 白名单外 → 策略拦截 (Blocked / blocked)
            var blockedCfg = new LocalTerminalConfig { AllowedCommands = new[] { "git" } };
            var blockedTool = new LocalTerminalTool(() => "test", _ => null, blockedCfg,
                (_, _, _) => Task.FromResult(LocalApprovalDecision.Allow), () => { });
            await blockedTool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "npm install" }, CancellationToken.None);

            // ② 用户拒绝 (Rejected / manual-rejected)
            var terminal = new LocalTerminalSessionViewModel();
            await terminal.EnsureStartedAsync(80, 24);
            try
            {
                var denyCfg = new LocalTerminalConfig { AllowedCommands = new[] { "git" } };
                var denyTool = new LocalTerminalTool(() => "test", _ => terminal, denyCfg,
                    (_, _, _) => Task.FromResult(LocalApprovalDecision.Deny), () => { });
                await denyTool.InvokeAsync(
                    new Dictionary<string, object?> { ["command"] = $"git audit-{marker}" }, CancellationToken.None);

                // ③ 实际执行 (Executed / manual-approved / 退出码)
                var runCfg = new LocalTerminalConfig { AllowedCommands = new[] { "Write-Output" }, TimeoutSeconds = 60 };
                var runTool = new LocalTerminalTool(() => "test", _ => terminal, runCfg,
                    (_, _, _) => Task.FromResult(LocalApprovalDecision.Allow), () => { });
                await runTool.InvokeAsync(
                    new Dictionary<string, object?> { ["command"] = $"Write-Output LITSSH_AUDIT_{marker}" }, CancellationToken.None);
            }
            finally
            {
                await terminal.DisposeAsync();
            }

            List<CommandAuditLog> mine;
            lock (records) mine = records.ToList();

            var blocked = Assert.Single(mine, r => r.Command.Contains("npm install"));
            Assert.Equal("local_shell", blocked.Tool);
            Assert.Equal(AuditCategory.Local, blocked.Category);
            Assert.Equal(CommandStatus.Blocked, blocked.Status);
            Assert.Equal("blocked", blocked.Decision);

            var denied = Assert.Single(mine, r => r.Command.Contains($"git audit-{marker}"));
            Assert.Equal(CommandStatus.Rejected, denied.Status);
            Assert.Equal("manual-rejected", denied.Decision);
            Assert.Equal("agent-audit-test", denied.SessionId);

            var executed = Assert.Single(mine, r => r.Command.Contains($"LITSSH_AUDIT_{marker}"));
            Assert.Equal(CommandStatus.Executed, executed.Status);
            Assert.Equal("manual-approved", executed.Decision);
            Assert.Equal(0, executed.ExitCode);
            Assert.Equal("agent-audit-test", executed.SessionId);
        }
        finally
        {
            LocalAudit.OverrideSink = _ => Task.CompletedTask;   // 还原为程序集默认空操作
            LocalAudit.AmbientSessionId = null;
        }
    }

    [Fact]
    public async Task Rejects_command_outside_allowlist()
    {
        var cfg = new LocalTerminalConfig { AllowedCommands = new[] { "git" } };
        var tool = new LocalTerminalTool(() => "test", _ => null, cfg, (_, _, _) => Task.FromResult(LocalApprovalDecision.Allow), () => { });

        var result = await tool.InvokeAsync(
            new Dictionary<string, object?> { ["command"] = "npm install" }, CancellationToken.None);

        Assert.Contains("白名单", result?.ToString());
    }

    [Fact]
    public async Task Denied_by_user_returns_rejection()
    {
        var terminal = new LocalTerminalSessionViewModel();
        await terminal.EnsureStartedAsync(80, 24);
        try
        {
            var cfg = new LocalTerminalConfig { AllowedCommands = new[] { "git" } };
            var tool = new LocalTerminalTool(() => "test", _ => terminal, cfg, (_, _, _) => Task.FromResult(LocalApprovalDecision.Deny), () => { });

            var result = await tool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "git status" }, CancellationToken.None);

            Assert.Contains("拒绝", result?.ToString());
        }
        finally
        {
            await terminal.DisposeAsync();
        }
    }

    [Fact]
    public async Task Runs_allowlisted_command_in_shared_terminal()
    {
        var terminal = new LocalTerminalSessionViewModel();
        await terminal.EnsureStartedAsync(80, 24);
        try
        {
            var cfg = new LocalTerminalConfig { AllowedCommands = new[] { "Write-Output" }, TimeoutSeconds = 60 };
            var tool = new LocalTerminalTool(() => "test", _ => terminal, cfg, (_, _, _) => Task.FromResult(LocalApprovalDecision.Allow), () => { });

            var result = await tool.InvokeAsync(
                new Dictionary<string, object?> { ["command"] = "Write-Output LITSSH_TOOL_OK" }, CancellationToken.None);

            Assert.Contains("LITSSH_TOOL_OK", result?.ToString());
            Assert.Contains("退出码 0", result?.ToString());
        }
        finally
        {
            await terminal.DisposeAsync();
        }
    }
}
