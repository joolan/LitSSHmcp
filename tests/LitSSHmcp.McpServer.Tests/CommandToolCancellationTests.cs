using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.McpServer.Services;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class CommandToolCancellationTests
{
    [Fact]
    public async Task Fleet_tool_forwards_cancellation_to_guarded_runner()
    {
        var guarded = new BlockingGuardedRunner();
        var tools = new CommandTools(null!, null!, null!, null!, null!, null!,
            null!, guarded);
        using var cts = new CancellationTokenSource();

        var running = tools.ExecuteFleet("s1,s2", "systemctl restart nginx",
            cancellationToken: cts.Token);
        await guarded.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(guarded.Token!.Value.IsCancellationRequested);
    }

    private sealed class BlockingGuardedRunner : IGuardedCommandService
    {
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken? Token { get; private set; }

        public Task<GuardedCommandOutcome> RunAsync(string serverReference, string command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public async Task<GuardedFleetOutcome> RunFleetAsync(IReadOnlyList<string> serverReferences,
            string command, int concurrency = 4, int maxTargets = 20, string? operationLabel = null,
            string? riskReport = null, CancellationToken ct = default,
            IProgress<GuardedFleetProgress>? progress = null, int timeoutSeconds = 60)
        {
            Token = ct;
            Entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return new GuardedFleetOutcome();
        }
    }
}
