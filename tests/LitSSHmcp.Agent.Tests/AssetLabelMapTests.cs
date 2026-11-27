using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class AssetLabelMapTests
{
    private const string Graph =
        "- ssh:abc | web01 | 10.0.0.1:22\n" +
        "- ds:m1 | MySQL orders | 10.0.0.3:3306 db=orders\n" +
        "- app:o1 | web order-service";

    [Fact]
    public void Formats_name_host_and_id()
    {
        var map = AssetLabelMap.FromPrompt(Graph);

        // ssh 资产额外给出可直接传给 MCP 的裸 serverId（ID 双形）。
        Assert.Equal("web01 (10.0.0.1:22) [ssh:abc | serverId=abc]", map.Describe("ssh:abc"));
        Assert.Equal("MySQL orders (10.0.0.3:3306) [ds:m1]", map.Describe("ds:m1"));
        Assert.Equal("web order-service [app:o1]", map.Describe("app:o1"));
    }

    [Fact]
    public void DescribeList_keeps_unknown_ids()
    {
        var map = AssetLabelMap.FromPrompt(Graph);

        Assert.Equal("web01 (10.0.0.1:22) [ssh:abc | serverId=abc], ssh:unknown",
            map.DescribeList("ssh:abc, ssh:unknown"));
        Assert.Equal(string.Empty, map.DescribeList(null));
        Assert.Null(AssetLabelMap.FromPrompt(null).Describe("ssh:abc"));
    }
}
