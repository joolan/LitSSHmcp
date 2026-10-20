using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AiTerminalBrokerTests
{
    [Fact]
    public async Task Opens_reads_lists_and_scopes_terminals_by_session()
    {
        await using var harness = new Harness();
        var marker = "LITSSH_MULTI_" + Guid.NewGuid().ToString("N")[..8];

        var opened = await harness.Broker.OpenAsync("s1", "probe", $"Write-Output {marker}",
            TimeSpan.FromSeconds(60), 4);
        Assert.True(opened.Success, opened.Error);
        var id = opened.Info!.Id;

        var read = await WaitForTextAsync(harness.Broker, "s1", id, marker);
        Assert.Contains(marker, read.Text);

        var list = harness.Broker.List("s1");
        Assert.Single(list);
        Assert.Equal(id, list[0].Id);
        Assert.Equal("probe", list[0].Name);

        Assert.Empty(harness.Broker.List("s2"));
        Assert.Null(harness.Broker.Read("s2", id, 0, 8000));
    }

    [Fact]
    public async Task Stop_terminates_a_long_running_terminal()
    {
        await using var harness = new Harness();
        var opened = await harness.Broker.OpenAsync("s1", "sleep", "Start-Sleep -Seconds 60",
            TimeSpan.FromSeconds(120), 4);
        Assert.True(opened.Success, opened.Error);
        var id = opened.Info!.Id;

        await Task.Delay(500);
        Assert.True(harness.Broker.List("s1").Single().Running);

        var stopped = await harness.Broker.StopAsync("s1", id);
        Assert.True(stopped.Success);
        Assert.Empty(harness.Broker.List("s1"));
    }

    [Fact]
    public async Task Run_injects_into_an_idle_terminal_without_closing_it()
    {
        await using var harness = new Harness();
        var opened = await harness.Broker.OpenAsync("s1", "probe", "Write-Output first",
            TimeSpan.FromSeconds(60), 4);
        Assert.True(opened.Success, opened.Error);
        var id = opened.Info!.Id;
        await WaitForIdleAsync(harness.Broker, "s1", id);

        var marker = "LITSSH_INJECT_" + Guid.NewGuid().ToString("N")[..8];
        var run = await harness.Broker.RunAsync("s1", id, $"Write-Output {marker}", TimeSpan.FromSeconds(60));

        Assert.True(run.Success, run.Error);
        Assert.Contains(marker, run.Result!.Output);
        Assert.Single(harness.Broker.List("s1"));
        var read = harness.Broker.Read("s1", id, 0, 8000);
        Assert.Contains(marker, read!.Text);
    }

    [Fact]
    public async Task Run_rejects_a_busy_terminal()
    {
        await using var harness = new Harness();
        var opened = await harness.Broker.OpenAsync("s1", "sleep", "Start-Sleep -Seconds 60",
            TimeSpan.FromSeconds(120), 4);
        Assert.True(opened.Success, opened.Error);

        var run = await harness.Broker.RunAsync("s1", opened.Info!.Id, "Write-Output late", TimeSpan.FromSeconds(10));

        Assert.False(run.Success);
        Assert.Contains("仍在执行", run.Error);
    }

    [Fact]
    public async Task Stop_closes_an_already_finished_terminal_tab()
    {
        await using var harness = new Harness();
        var opened = await harness.Broker.OpenAsync("s1", "done", "Write-Output done",
            TimeSpan.FromSeconds(60), 4);
        Assert.True(opened.Success, opened.Error);
        await WaitForIdleAsync(harness.Broker, "s1", opened.Info!.Id);

        var stopped = await harness.Broker.StopAsync("s1", opened.Info.Id);

        Assert.True(stopped.Success);
        Assert.Empty(harness.Broker.List("s1"));
    }

    [Fact]
    public async Task Rejects_new_terminal_when_all_sessions_are_running()
    {
        await using var harness = new Harness();
        var first = await harness.Broker.OpenAsync("s1", "one", "Start-Sleep -Seconds 60", TimeSpan.FromSeconds(120), 2);
        var second = await harness.Broker.OpenAsync("s1", "two", "Start-Sleep -Seconds 60", TimeSpan.FromSeconds(120), 2);
        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);

        var third = await harness.Broker.OpenAsync("s1", "three", "Start-Sleep -Seconds 60", TimeSpan.FromSeconds(120), 2);

        Assert.False(third.Success);
        Assert.Contains("最多", third.Error);
    }

    [Fact]
    public async Task Close_session_removes_only_that_sessions_terminals()
    {
        await using var harness = new Harness();
        var a = await harness.Broker.OpenAsync("s1", "a", "Start-Sleep -Seconds 60", TimeSpan.FromSeconds(120), 4);
        var b = await harness.Broker.OpenAsync("s2", "b", "Start-Sleep -Seconds 60", TimeSpan.FromSeconds(120), 4);
        Assert.True(a.Success, a.Error);
        Assert.True(b.Success, b.Error);

        harness.Broker.CloseSession("s1");

        Assert.Empty(harness.Broker.List("s1"));
        Assert.Single(harness.Broker.List("s2"));
    }

    private static async Task WaitForIdleAsync(IAiTerminalBroker broker, string sessionKey, string terminalId)
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

    private static async Task<AiTerminalReadResult> WaitForTextAsync(
        IAiTerminalBroker broker, string sessionKey, string terminalId, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        AiTerminalReadResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = broker.Read(sessionKey, terminalId, 0, 8000);
            if (last?.Text.Contains(expected, StringComparison.Ordinal) == true)
                return last;
            await Task.Delay(100);
        }
        Assert.True(last is not null, "terminal output was not available");
        Assert.Contains(expected, last!.Text);
        return last;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<LocalTerminalSessionViewModel> _sessions = new();

        public Harness()
        {
            Broker = new AiTerminalBroker(
                name =>
                {
                    var vm = new LocalTerminalSessionViewModel(isAi: true) { SessionName = name };
                    lock (_gate) _sessions.Add(vm);
                    _ = vm.EnsureStartedAsync(80, 24);
                    return vm;
                },
                vm =>
                {
                    lock (_gate) _sessions.Remove(vm);
                    _ = vm.DisposeAsync();
                },
                _ => { });
        }

        public AiTerminalBroker Broker { get; }

        public async ValueTask DisposeAsync()
        {
            Broker.CloseAll();
            List<LocalTerminalSessionViewModel> copy;
            lock (_gate) copy = _sessions.ToList();
            foreach (var vm in copy)
                await vm.DisposeAsync();
        }
    }
}
