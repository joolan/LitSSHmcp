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
        Assert.Equal(0, searchCalls);
    }

    [Fact]
    public async Task Falls_back_to_search_when_asset_not_in_prompt_graph()
    {
        var searched = new List<string>();
        var topology = new FakeTool("topology_query", args =>
        {
            if (Equals(args!["action"], "search"))
            {
                searched.Add((string)args["query"]!);
                return Json("""{"items":[{"node":{"id":"service:s1:nginx","type":"service"}}]}""");
            }
            Assert.Equal("neighbors", args["action"]);
            Assert.Equal("service:s1:nginx", args["assetId"]);
            return Json("""
                {"root":{"id":"service:s1:nginx","type":"service"},
                 "nodes":[{"id":"service:s1:nginx","type":"service"}],
                 "edges":[]}
                """);
        });
        var provider = AssetGraphContextProvider.Create(topology, null);

        var context = await provider!("nginx 为什么没启动", CancellationToken.None);

        Assert.NotNull(context);
        Assert.Contains("service:s1:nginx (service)", context!.Text);
        Assert.Equal(new[] { "service:s1:nginx" }, context.AssetIds);
        Assert.Contains("nginx", searched);
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
