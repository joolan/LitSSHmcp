using System.Threading;
using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class LocalTerminalToolTests
{
    [Fact]
    public async Task Rejects_command_outside_allowlist()
    {
        var cfg = new LocalTerminalConfig { AllowedCommands = new[] { "git" } };
        var tool = new LocalTerminalTool(() => null, cfg, (_, _) => LocalApprovalDecision.Allow, () => { });

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
            var tool = new LocalTerminalTool(() => terminal, cfg, (_, _) => LocalApprovalDecision.Deny, () => { });

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
            var tool = new LocalTerminalTool(() => terminal, cfg, (_, _) => LocalApprovalDecision.Allow, () => { });

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
