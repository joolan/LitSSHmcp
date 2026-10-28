using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class InsightToolsTests
{
    [Fact]
    public async Task Dispatches_timeline_and_asset_profile_actions()
    {
        var timeline = new CapturingTool("ops_timeline", "timeline-ok");
        var profile = new CapturingTool("ops_asset_profile", "profile-ok");
        var tool = InsightTools.Create(timeline, profile);

        Assert.Equal("ops_insight", tool.Name);
        Assert.True(tool.ReadOnly);

        var t = await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["action"] = "timeline",
            ["incidentId"] = 7
        }, default);
        Assert.Equal("timeline-ok", t!.ToString());
        Assert.Equal(7, timeline.LastArgs!["incidentId"]);

        var p = await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["action"] = "asset_profile",
            ["assetId"] = "ssh:s1"
        }, default);
        Assert.Equal("profile-ok", p!.ToString());
        Assert.Equal("ssh:s1", profile.LastArgs!["assetId"]);

        var bad = await tool.InvokeAsync(new Dictionary<string, object?> { ["action"] = "unknown" }, default);
        Assert.Contains("仅支持", bad!.ToString());
    }

    private sealed class CapturingTool(string name, object result) : IAgentTool
    {
        public string Name { get; } = name;
        public AITool Tool => null!;
        public bool Destructive => false;
        public bool ReadOnly => true;
        public string? ToolGroup => null;
        public IDictionary<string, object?>? LastArgs { get; private set; }

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            LastArgs = arguments;
            return Task.FromResult<object?>(result);
        }
    }
}
