using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Web.Sessions;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Web.Tests;

public class WebRuntimeDefaultsTests
{
    [Fact]
    public async Task Default_interface_methods_are_safe_no_ops()
    {
        IWebRuntime runtime = new MinimalRuntime();

        runtime.IndexTurnInBackground("1", "user", "assistant");
        var host = await runtime.InvokeHostToolAsync("job_list", new Dictionary<string, object?>(), default);
        var builtin = await runtime.InvokeBuiltinToolAsync("job_list", new Dictionary<string, object?>(), default);
        var plan = await runtime.GetFleetPlanAsync("owner", 1, "plan", default);
        var plans = await runtime.ListFleetPlansAsync("owner", 1, default);

        Assert.NotNull(host);
        Assert.Equal(host?.ToString(), builtin?.ToString());
        Assert.Null(plan);
        Assert.Empty(plans);
    }

    private sealed class MinimalRuntime : IWebRuntime
    {
        public string Summary => "minimal";
        public Task SendAsync(IReadOnlyList<AIContent> contents, IProgress<AgentEvent>? progress,
            CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
