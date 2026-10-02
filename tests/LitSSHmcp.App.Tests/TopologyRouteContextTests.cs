using System.Windows;
using LitSSHmcp.App.Services;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TopologyRouteContextTests
{
    private static readonly (double X, double Y, double W, double H) FromBox = (20, 20, 60, 40);
    private static readonly (double X, double Y, double W, double H) ToBox = (300, 0, 60, 40);

    private static Rect[] BuildObstacles() => new[]
    {
        new Rect(0, 0, 200, 200),    // 包含 fromBox 的容器（应被排除、不阻挡）
        new Rect(150, -60, 40, 180), // 普通障碍 A
        new Rect(280, -40, 30, 120)  // 普通障碍 B
    };

    [Fact]
    public void RouteContext_PathClear_MatchesFilteredBruteForce()
    {
        var obstacles = BuildObstacles();
        var fromRect = new Rect(FromBox.X, FromBox.Y, FromBox.W, FromBox.H);
        var toRect = new Rect(ToBox.X, ToBox.Y, ToBox.W, ToBox.H);

        var filtered = obstacles.Where(o => !o.Contains(fromRect) && !o.Contains(toRect)).ToArray();
        Assert.Equal(2, filtered.Length); // 容器包含 fromBox → 被排除

        var ctx = new TopologyRouteEngine.RouteContext(obstacles);

        for (var i = 0; i <= 20; i++)
        {
            for (var j = 0; j <= 20; j++)
            {
                var x = i * 20.0;
                var y = j * 20.0;
                var probes = new[]
                {
                    (X1: x, Y1: y, X2: x + 40, Y2: y),
                    (X1: x, Y1: y, X2: x, Y2: y + 40),
                    (X1: x, Y1: y, X2: x - 60, Y2: y),
                    (X1: x, Y1: y, X2: x, Y2: y - 60)
                };

                foreach (var (x1, y1, x2, y2) in probes)
                {
                    var brute = TopologyRouteEngine.SegmentClear(x1, y1, x2, y2, filtered);
                    var indexed = ctx.PathClear(new[] { new Point(x1, y1), new Point(x2, y2) }, fromRect, toRect);
                    Assert.Equal(brute, indexed);
                }
            }
        }
    }

    [Fact]
    public void RouteContext_ContainerDoesNotBlock_RawBruteDoes()
    {
        var obstacles = BuildObstacles();
        var fromRect = new Rect(FromBox.X, FromBox.Y, FromBox.W, FromBox.H);
        var toRect = new Rect(ToBox.X, ToBox.Y, ToBox.W, ToBox.H);
        var ctx = new TopologyRouteEngine.RouteContext(obstacles);

        // 穿越容器内部的水平线段（不碰 A/B）：容器透明
        var probe = new[] { new Point(10, 60), new Point(50, 60) };

        Assert.False(TopologyRouteEngine.PathClear(probe, obstacles));                 // 未过滤：容器阻挡
        Assert.True(TopologyRouteEngine.PathClear(probe, obstacles.Where(o => !o.Contains(fromRect) && !o.Contains(toRect)).ToArray()));
        Assert.True(ctx.PathClear(probe, fromRect, toRect));                            // 上下文：等价于过滤版
    }

    [Fact]
    public void BuildRoute_SharedContext_MatchesPerCall()
    {
        var obstacles = new List<Rect>();
        for (var k = 0; k < 5; k++)
            obstacles.Add(new Rect(130 + k * 160, -40, 40, 300));

        var ctx = new TopologyRouteEngine.RouteContext(obstacles);
        var exercisedAstar = false;

        for (var i = 0; i < 8; i++)
        {
            var from = (i * 90.0, 0.0, 60.0, 40.0);
            var to = (300.0 + i * 90.0, 220.0, 60.0, 40.0);
            var route = TopologyRouteEngine.ComputeRoute($"app:{i}", "app:target", "connectsTo", false, from, to, null);

            // 确认至少部分路由必须走 A*（回退 Z 形被障碍挡住）
            if (!TopologyRouteEngine.PathClear(TopologyRouteEngine.FallbackRoute(route), obstacles))
                exercisedAstar = true;

            var perCall = TopologyRouteEngine.BuildRoute(route, obstacles, fast: false);
            var shared = TopologyRouteEngine.BuildRoute(route, ctx, fast: false);

            Assert.Equal(perCall, shared);
        }

        Assert.True(exercisedAstar, "测试数据应触发 A* 避障路径");
    }

    [Fact]
    public void BuildRoute_SharedContext_KeepsEndpointsOnBoxes()
    {
        var obstacles = new List<Rect> { new(150, -60, 40, 180) };
        var ctx = new TopologyRouteEngine.RouteContext(obstacles);

        var from = (X: 0.0, Y: 0.0, W: 60.0, H: 40.0);
        var to = (X: 300.0, Y: 0.0, W: 60.0, H: 40.0);
        var route = TopologyRouteEngine.ComputeRoute("app:a", "app:b", "connectsTo", false, from, to, null);

        var points = TopologyRouteEngine.BuildRoute(route, ctx, fast: false);

        Assert.True(points.Count >= 2);
        Assert.True(TopologyRouteEngine.PathClear(points, obstacles));
        // 端点必须仍落在节点盒（选中框）边界上——共享上下文不得改变端点锚定
        var fromRect = new Rect(from.X, from.Y, from.W, from.H);
        var toRect = new Rect(to.X, to.Y, to.W, to.H);
        Assert.True(Rect.Inflate(fromRect, 0.01, 0.01).Contains(points[0]),
            $"起点 {points[0]} 应在 fromBox 边界");
        Assert.True(Rect.Inflate(toRect, 0.01, 0.01).Contains(points[^1]),
            $"终点 {points[^1]} 应在 toBox 边界");
    }
}
