using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TopologyLayoutHistoryTests
{
    [Fact]
    public void Undo_redo_and_clear_round_trip()
    {
        var store = new MemoryLayoutStore(new TopologyLayout());
        var history = new TopologyLayoutHistory(store);

        history.PushUndo();
        var manual = new TopologyLayout { IsManual = true };
        manual.Nodes["n1"] = new NodeLayout { X = 10, Y = 20, Width = 30, Height = 40 };
        history.Save(manual);

        Assert.False(history.CanRedo);
        Assert.True(history.Undo());
        Assert.False(history.Get().IsManual);          // 回到最初自动布局
        Assert.True(history.CanRedo);

        Assert.True(history.Redo());
        Assert.True(history.Get().IsManual);
        Assert.Equal(10, history.Get().Nodes["n1"].X);

        history.PushUndo();
        history.Clear();
        Assert.False(store.Saved.IsManual);
        Assert.True(history.CanUndo);                   // 清空仍可撤销
    }

    private sealed class MemoryLayoutStore(TopologyLayout initial) : ITopologyLayoutStore
    {
        public TopologyLayout Saved { get; private set; } = initial;
        public string Path => "(memory)";
        public TopologyLayout Load() => Saved;
        public void Save(TopologyLayout layout) => Saved = layout;
        public void Clear() => Saved = new TopologyLayout();
    }
}
