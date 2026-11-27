using System.Text.Json;
using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class AssetAttributionTests
{
    private const string Graph =
        "- ssh:abc123 | web01 | 10.0.0.1:22\n" +
        "- ssh:def456 | db01 | 10.0.0.2:22\n" +
        "- ds:mysql1 | MySQL orders | 10.0.0.3:3306 db=orders\n" +
        "- app:ord1 | web order-service | 10.0.0.4";

    [Fact]
    public void Resolves_server_by_name_host_and_bare_id()
    {
        Assert.Equal(new[] { "ssh:abc123" }, Resolve(("serverId", "web01")));
        Assert.Equal(new[] { "ssh:abc123" }, Resolve(("serverId", "10.0.0.1")));
        Assert.Equal(new[] { "ssh:abc123" }, Resolve(("serverId", "abc123")));
    }

    [Fact]
    public void Resolves_multiple_servers_and_preserves_explicit_ids()
    {
        Assert.Equal(new[] { "ssh:abc123", "ssh:def456" }, Resolve(("serverIds", "web01, db01")));
        // 资产清单可能被截断；显式带前缀的 ID 不丢。
        Assert.Equal(new[] { "ssh:remote999" }, Resolve(("assetId", "ssh:remote999")));
    }

    [Fact]
    public void Resolves_datasource_by_label_and_name_part()
    {
        Assert.Equal(new[] { "ds:mysql1" }, Resolve(("dataSourceId", "orders")));
        Assert.Equal(new[] { "ds:mysql1" }, Resolve(("dataSourceId", "MySQL orders")));
    }

    [Fact]
    public void Ignores_non_target_arguments()
    {
        Assert.Empty(Resolve(("command", "ssh web01")));
        Assert.Empty(Resolve(("path", "/var/log/web01/nginx.log")));
    }

    [Fact]
    public void Scans_arrays_and_nested_objects_under_target_keys()
    {
        Assert.Equal(new[] { "ssh:abc123", "ssh:def456" },
            Resolve(("targets", new object[] { "web01", "db01" })));

        using var document = JsonDocument.Parse("""{"server":"web01","command":"nginx -T"}""");
        Assert.Equal(new[] { "ssh:abc123" },
            AssetAttribution.FromArguments(Graph,
                new Dictionary<string, object?> { ["target"] = document.RootElement }));
    }

    private static IReadOnlyList<string> Resolve(params (string Key, object Value)[] pairs)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            arguments[key] = value;
        return AssetAttribution.FromArguments(Graph, arguments);
    }
}
