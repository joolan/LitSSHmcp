using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

[Collection("local-audit")]
public class LocalShellSessionToolTests
{
    [Fact]
    public async Task Open_list_read_and_stop_round_trip()
    {
        await using var harness = new Harness();
        var marker = "LITSSH_TOOL_" + Guid.NewGuid().ToString("N")[..8];
        var cfg = new LocalTerminalConfig
        {
            AllowedCommands = new[] { "Write-Output", "Start-Sleep" },
            MaxSessions = 4
        };

        var open = new LocalShellOpenTool(() => "test", harness.Broker, cfg,
            (_, _, _) => Task.FromResult(LocalApprovalDecision.Allow), () => { });
        var opened = await open.InvokeAsync(new Dictionary<string, object?>
        {
            ["name"] = "probe",
            ["command"] = $"Write-Output {marker}"
        }, CancellationToken.None);
        Assert.Contains("term-1", opened?.ToString());

        var read = await WaitForReadAsync(harness.Broker, "test", "term-1", marker);
        Assert.Contains(marker, read);

        var list = new LocalShellListTool(() => "test", harness.Broker);
        var listText = (await list.InvokeAsync(null, CancellationToken.None))?.ToString();
        Assert.Contains("term-1", listText);
        Assert.Contains("probe", listText);

        var readTool = new LocalShellReadTool(() => "test", harness.Broker);
        var readText = (await readTool.InvokeAsync(new Dictionary<string, object?>
        {
            ["terminalId"] = "term-1",
            ["cursor"] = 0,
            ["maxChars"] = 8000
        }, CancellationToken.None))?.ToString();
        Assert.Contains(marker, readText);
        Assert.Contains("cursor", readText);

        var stop = new LocalShellStopTool(() => "test", harness.Broker);
        var stopText = (await stop.InvokeAsync(new Dictionary<string, object?>
        {
            ["terminalId"] = "term-1"
        }, CancellationToken.None))?.ToString();
        Assert.Contains("term-1", stopText);
        Assert.Empty(harness.Broker.List("test"));
    }

    [Fact]
    public async Task Local_shell_injects_into_an_idle_opened_terminal()
    {
        await using var harness = new Harness();
        var first = "LITSSH_FIRST_" + Guid.NewGuid().ToString("N")[..8];
        var second = "LITSSH_SECOND_" + Guid.NewGuid().ToString("N")[..8];
        var cfg = new LocalTerminalConfig
        {
            AllowedCommands = new[] { "Write-Output" },
            MaxSessions = 4
        };

        var open = new LocalShellOpenTool(() => "test", harness.Broker, cfg,
            (_, _, _) => Task.FromResult(LocalApprovalDecision.Allow), () => { });
        var opened = await open.InvokeAsync(new Dictionary<string, object?>
        {
            ["name"] = "probe",
            ["command"] = $"Write-Output {first}"
        }, CancellationToken.None);
        Assert.Contains("term-1", opened?.ToString());
        await WaitForIdleAsync(harness.Broker, "test", "term-1");

        var shell = new LocalTerminalTool(() => "test", _ => null, cfg,
            (_, _, _) => Task.FromResult(LocalApprovalDecision.Allow), () => { },
            broker: harness.Broker);
        var injected = await shell.InvokeAsync(new Dictionary<string, object?>
        {
            ["command"] = $"Write-Output {second}",
            ["terminalId"] = "term-1"
        }, CancellationToken.None);

        Assert.Contains(second, injected?.ToString());
        Assert.Single(harness.Broker.List("test"));
    }

    private static async Task WaitForIdleAsync(AiTerminalBroker broker, string sessionKey, string terminalId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var info = broker.List(sessionKey).FirstOrDefault(x => x.Id == terminalId);
            if (info is { Running: false })
                return;
            await Task.Delay(100);
        }
        Assert.Fail("terminal did not become idle");
    }

    [Fact]
    public async Task Open_respects_user_rejection()
    {
        await using var harness = new Harness();
        var cfg = new LocalTerminalConfig { AllowedCommands = new[] { "Write-Output" } };
        var open = new LocalShellOpenTool(() => "test", harness.Broker, cfg,
            (_, _, _) => Task.FromResult(LocalApprovalDecision.Deny), () => { });

        var result = await open.InvokeAsync(new Dictionary<string, object?>
        {
            ["name"] = "denied",
            ["command"] = "Write-Output SHOULD_NOT_START"
        }, CancellationToken.None);

        Assert.Contains("拒绝", result?.ToString());
        Assert.Empty(harness.Broker.List("test"));
    }

    [Fact]
    public async Task Open_rejects_interactive_shell()
    {
        await using var harness = new Harness();
        var cfg = new LocalTerminalConfig { AllowedCommands = new[] { "powershell" } };
        var approvals = 0;
        var open = new LocalShellOpenTool(() => "test", harness.Broker, cfg,
            (_, _, _) => { approvals++; return Task.FromResult(LocalApprovalDecision.Allow); }, () => { });

        var result = await open.InvokeAsync(new Dictionary<string, object?>
        {
            ["name"] = "shell",
            ["command"] = "powershell -Command Get-Date"
        }, CancellationToken.None);

        Assert.Contains("交互", result?.ToString());
        Assert.Equal(0, approvals);
        Assert.Empty(harness.Broker.List("test"));
    }

    private static async Task<string> WaitForReadAsync(
        AiTerminalBroker broker, string sessionKey, string terminalId, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var read = broker.Read(sessionKey, terminalId, 0, 8000);
            if (read?.Text.Contains(expected, StringComparison.Ordinal) == true)
                return read.Text;
            await Task.Delay(100);
        }
        Assert.Fail("terminal output was not available");
        return string.Empty;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<LocalTerminalSessionViewModel> _sessions = new();

        public Harness()
        {
            Broker = new AiTerminalBroker(
                name =>
                {
                    var vm = new LocalTerminalSessionViewModel(isAi: true) { SessionName = name };
                    _sessions.Add(vm);
                    _ = vm.EnsureStartedAsync(80, 24);
                    return vm;
                },
                vm =>
                {
                    _sessions.Remove(vm);
                    _ = vm.DisposeAsync();
                },
                _ => { });
        }

        public AiTerminalBroker Broker { get; }

        public async ValueTask DisposeAsync()
        {
            Broker.CloseAll();
            foreach (var vm in _sessions.ToList())
                await vm.DisposeAsync();
        }
    }
}
