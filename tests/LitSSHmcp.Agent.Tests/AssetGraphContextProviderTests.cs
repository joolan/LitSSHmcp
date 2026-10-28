using System.Text.Json;
using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class AssetGraphContextProviderTests
{
    [Fact]
    public async Task Builds_neighborhood_for_known_asset_without_search()
    {
        var searchCalls = 0;
        var topology = new FakeTool("topology_query", args =>
        {
            if (Equals(args!["action"], "search"))
            {
                searchCalls++;
                return Json("""{"items":[]}""");
            }
            Assert.Equal("neighbors", args["action"]);
            Assert.Equal("app:order", args["assetId"]);
            Assert.Equal(1, args["depth"]);
            return Json("""
                {"root":{"id":"app:order","type":"application"},
                 "nodes":[{"id":"app:order","type":"application"},{"id":"ssh:s1","type":"ssh"}],
                 "edges":[{"from":"app:order","to":"ssh:s1","type":"runsOn"}]}
                """);
        });
        var graph = "- app:order | order-service | 10.0.0.1\n- ssh:s1 | web1";
        var provider = AssetGraphContextProvider.Create(topology, graph);

        var context = await provider!("check app:order health", CancellationToken.None);

        Assert.NotNull(context);
        Assert.Contains("app:order (application)", context!.Text);
        Assert.Contains("app:order -runsOn-> ssh:s1", context.Text);
        Assert.Equal(new[] { "app:order" }, context.AssetIds);
        Assert.Contains("ssh:s1", context.RelatedAssetIds ?? Array.Empty<string>());
        Assert.Equal(0, searchCalls);
    }

    [Fact]
    public async Task Returns_null_for_generic_query_without_known_or_explicit_asset()
    {
        var topologyCalls = 0;
        var topology = new FakeTool("topology_query", _ =>
        {
            topologyCalls++;
            return Json("""{"items":[]}""");
        });
        var provider = AssetGraphContextProvider.Create(topology, null);

        var context = await provider!("nginx 为什么没启动", CancellationToken.None);

        Assert.Null(context);
        Assert.Equal(0, topologyCalls);   // 不再对普通词做 topology search
    }

    [Fact]
    public async Task Uses_explicit_asset_id_without_search()
    {
        var topology = new FakeTool("topology_query", args =>
        {
            Assert.Equal("neighbors", args!["action"]);
            Assert.Equal("ssh:s1", args["assetId"]);
            return Json("""
                {"root":{"id":"ssh:s1","type":"ssh"},
                 "nodes":[{"id":"ssh:s1","type":"ssh"}],
                 "edges":[]}
                """);
        });
        var provider = AssetGraphContextProvider.Create(topology, null);

        var context = await provider!("check ssh:s1", CancellationToken.None);

        Assert.NotNull(context);
        Assert.Contains("ssh:s1 (ssh)", context!.Text);
        Assert.Equal(new[] { "ssh:s1" }, context.AssetIds);
    }

    [Fact]
    public void Returns_null_when_topology_tools_are_missing()
    {
        Assert.Null(AssetGraphContextProvider.Create(null, "- ssh:s1 | web1"));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeTool(string name, Func<IDictionary<string, object?>?, object?> handler) : IAgentTool
    {
        public string Name => name;
        public bool Destructive => false;
        public bool ReadOnly => true;
        public string? ToolGroup => "topology";
        public AITool Tool => null!;

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default) =>
            Task.FromResult(handler(arguments));
    }
}
