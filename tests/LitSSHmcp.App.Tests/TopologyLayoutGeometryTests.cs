using System.Windows;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TopologyLayoutGeometryTests
{
    private const double Header = 36;

    [Fact]
    public void ClampChildTo_ChildOutside_ClampsInsideWithPadding()
    {
        var parent = new Rect(0, 0, 300, 200);
        var child = new Rect(-50, -50, 120, 30);

        var r = TopologyViewModel.ClampChildTo(parent, child, Header);

        Assert.True(r.X >= parent.X + 6 - 0.01, $"x={r.X}");
        Assert.True(r.Y >= parent.Y + Header - 6 - 0.01, $"y={r.Y}");
        Assert.True(r.Right <= parent.Right - 6 + 0.01, $"right={r.Right}");
        Assert.True(r.Bottom <= parent.Bottom - 6 + 0.01, $"bottom={r.Bottom}");
        Assert.Equal(120, r.Width);
        Assert.Equal(30, r.Height);
    }

    [Fact]
    public void ClampChildTo_ChildAlreadyInside_Unchanged()
    {
        var parent = new Rect(10, 20, 300, 200);
        var child = new Rect(50, 80, 120, 30);

        var r = TopologyViewModel.ClampChildTo(parent, child, Header);

        Assert.Equal(child.X, r.X);
        Assert.Equal(child.Y, r.Y);
        Assert.Equal(child.Width, r.Width);
        Assert.Equal(child.Height, r.Height);
    }

    [Fact]
    public void ClampChildTo_ChildOverflowsRight_BottomPinned()
    {
        var parent = new Rect(0, 0, 200, 100);
        var child = new Rect(500, 500, 120, 30);

        var r = TopologyViewModel.ClampChildTo(parent, child, Header);

        Assert.Equal(parent.X + parent.Width - child.Width - 6, r.X, 3);
        Assert.Equal(parent.Y + parent.Height - child.Height - 6, r.Y, 3);
    }

    [Fact]
    public void ClampChildTo_ParentSmallerThanChild_CentersInsteadOfInverting()
    {
        // 容器缩到比子节点还小时：Clamp 走 (min+max)/2 居中，而非反向钳制
        var parent = new Rect(0, 0, 80, 36);
        var child = new Rect(0, 0, 120, 30);

        var r = TopologyViewModel.ClampChildTo(parent, child, Header);

        var minX = parent.X + 6;
        var maxX = parent.X + parent.Width - child.Width - 6;
        Assert.True(maxX < minX, "前提：可用空间应为反转");
        Assert.Equal((minX + maxX) / 2, r.X, 3);
        Assert.Equal(120, r.Width);
        Assert.Equal(30, r.Height);
    }

    [Fact]
    public void ComputeResize_SouthEast_DerivedFromStartPlusTotal()
    {
        var start = new Rect(100, 100, 200, 100);

        var r = TopologyViewModel.ComputeResize("se", start, 4, -7);

        Assert.Equal(100, r.X);
        Assert.Equal(100, r.Y);
        Assert.Equal(200, r.W);
        Assert.Equal(90, r.H);
    }

    [Fact]
    public void ComputeResize_Oscillation_NoIncrementalDrift()
    {
        var start = new Rect(100, 100, 200, 100);

        // 新模型：结果只取决于「起点 + 总位移」——+4 再 -6 与直接 -2 完全等价
        var direct = TopologyViewModel.ComputeResize("e", start, -2, 0);
        Assert.Equal(200, direct.W);

        // 旧增量模型（每步吸附）：+4 再 -6 会把吸附误差累积成 10px 漂移
        var incremental = start.Width;
        incremental = Math.Round((incremental + 4) / 10.0) * 10.0;
        incremental = Math.Round((incremental - 6) / 10.0) * 10.0;
        Assert.Equal(190, incremental);
        Assert.NotEqual(incremental, direct.W);
    }

    [Fact]
    public void ComputeResize_NorthWest_OppositeCornerPinned()
    {
        var start = new Rect(100, 100, 200, 100);

        var r = TopologyViewModel.ComputeResize("nw", start, 7, 7);

        Assert.Equal(110, r.X);
        Assert.Equal(110, r.Y);
        Assert.Equal(190, r.W);
        Assert.Equal(90, r.H);
        Assert.Equal(start.X + start.Width, r.X + r.W);
        Assert.Equal(start.Y + start.Height, r.Y + r.H);
    }

    [Fact]
    public void ComputeResize_West_HugeDrag_ClampsMinWidthPinsRight()
    {
        var start = new Rect(100, 100, 200, 100);

        var r = TopologyViewModel.ComputeResize("w", start, 500, 0);

        Assert.Equal(80, r.W);
        Assert.Equal(220, r.X);
        Assert.Equal(300, r.X + r.W);
    }

    [Fact]
    public void ComputeResize_North_HugeDrag_ClampsMinHeightPinsBottom()
    {
        var start = new Rect(100, 100, 200, 100);

        var r = TopologyViewModel.ComputeResize("n", start, 0, 500);

        Assert.True(r.H >= 36, $"h={r.H}");
        Assert.Equal(200, r.Y + r.H);
    }

    [Fact]
    public void EdgeHandleAt_MidOfEachSide_ReturnsSide()
    {
        var sel = new Rect(100, 100, 200, 100);

        Assert.Equal("n", TopologyViewModel.EdgeHandleAt(sel, new Point(200, 103)));
        Assert.Equal("s", TopologyViewModel.EdgeHandleAt(sel, new Point(200, 197)));
        Assert.Equal("w", TopologyViewModel.EdgeHandleAt(sel, new Point(103, 150)));
        Assert.Equal("e", TopologyViewModel.EdgeHandleAt(sel, new Point(297, 150)));
    }

    [Fact]
    public void EdgeHandleAt_CornersAndCenter_AreNotEdgeBands()
    {
        var sel = new Rect(100, 100, 200, 100);

        // 角落区域归角手柄（对角缩放），中心区域用于移动节点
        Assert.Null(TopologyViewModel.EdgeHandleAt(sel, new Point(101, 101)));
        Assert.Null(TopologyViewModel.EdgeHandleAt(sel, new Point(299, 199)));
        Assert.Null(TopologyViewModel.EdgeHandleAt(sel, new Point(200, 150)));

        // 选中框外侧不算边带
        Assert.Null(TopologyViewModel.EdgeHandleAt(sel, new Point(50, 150)));
        Assert.Null(TopologyViewModel.EdgeHandleAt(sel, new Point(200, 80)));
    }

    [Fact]
    public void EdgeHandleAt_TooSmallNode_ReturnsNull()
    {
        // 小于 2*cornerGap 的节点没有可用边带（只保留角手柄）
        Assert.Null(TopologyViewModel.EdgeHandleAt(new Rect(0, 0, 10, 10), new Point(5, 0)));
    }

    [Fact]
    public void PortCenterFor_IsOutsideBorderWithGap()
    {
        var r = new Rect(100, 100, 200, 100);
        const double off = 8; // PortRadius 5 + PortGap 3

        var left = TopologyViewModel.PortCenterFor(r, "left");
        var right = TopologyViewModel.PortCenterFor(r, "right");
        var top = TopologyViewModel.PortCenterFor(r, "top");
        var bottom = TopologyViewModel.PortCenterFor(r, "bottom");

        Assert.Equal(r.X - off, left.X);
        Assert.Equal(r.Y + r.Height / 2, left.Y);
        Assert.Equal(r.Right + off, right.X);
        Assert.Equal(r.Y - off, top.Y);
        Assert.Equal(r.Bottom + off, bottom.Y);
        Assert.Equal(r.X + r.Width / 2, top.X);
        Assert.Equal(r.X + r.Width / 2, bottom.X);

        // 端口命中盒（半径5+容差2）不越过边界线，与边缩放带(内侧6px)互不重叠
        Assert.True(left.X + 5 + 2 < r.X, $"port right={left.X + 7}");
        Assert.True(top.Y + 5 + 2 < r.Y, $"port bottom={top.Y + 7}");
    }
}
