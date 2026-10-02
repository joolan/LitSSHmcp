using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

public class TopologyLayoutTests
{
    [Fact]
    public void Layout_round_trips_node_and_anchor()
    {
        var path = Path.Combine(Path.GetTempPath(), "litssh-layout-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new TopologyLayoutStore(path);

        try
        {
            var layout = new TopologyLayout { IsManual = true };
            layout.Nodes["ssh:1"] = new NodeLayout { X = 10, Y = 20, Width = 300, Height = 120 };
            layout.Edges["ds:mysql|runsOn|ssh:1"] = new EdgeLayout
            {
                From = new AnchorSpec { Side = "bottom", Ratio = 0.3 },
                To = new AnchorSpec { Side = "top", Ratio = 0.5 }
            };

            store.Save(layout);
            var loaded = store.Load();

            Assert.True(loaded.IsManual);
            Assert.Equal(300, loaded.Nodes["ssh:1"].Width);
            Assert.Equal("bottom", loaded.Edges["ds:mysql|runsOn|ssh:1"].From!.Side);
            Assert.Equal(0.3, loaded.Edges["ds:mysql|runsOn|ssh:1"].From!.Ratio);
        }
        finally
        {
            store.Clear();
        }
    }

    [Fact]
    public void Missing_file_yields_empty_layout()
    {
        var store = new TopologyLayoutStore(Path.Combine(Path.GetTempPath(), "litssh-none-" + Guid.NewGuid().ToString("N") + ".json"));
        var layout = store.Load();
        Assert.False(layout.IsManual);
        Assert.Empty(layout.Nodes);
    }

    [Fact]
    public void RunsOn_is_unique_per_source_node()
    {
        var existing = new[]
        {
            new RelationConfig { From = "ds:a", To = "ssh:1", Type = "runsOn" }
        };

        Assert.False(RelationRules.TryValidate("ds:a", "ssh:2", "runsOn", existing, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));

        // 同一目标重复（由调用方判重）通过基础校验
        Assert.True(RelationRules.TryValidate("ds:a", "ssh:1", "runsOn", existing, out _));

        // 其它节点不受影响
        Assert.True(RelationRules.TryValidate("ds:b", "ssh:2", "runsOn", existing, out _));

        // 非 runsOn 不受该约束
        Assert.True(RelationRules.TryValidate("ds:a", "ssh:2", "relatedTo", existing, out _));
    }
}
