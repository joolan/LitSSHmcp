using System.Reflection;
using System.Windows;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TopologyRouteEngineTests
{
    [Fact]
    public void FallbackRoute_IsOrthogonalZ()
    {
        var route = new TopologyRouteEngine.EdgeRoute
        {
            Horizontal = true,
            S = new Point(0, 0),
            T = new Point(100, 0),
            FromBox = (0, 0, 60, 40),
            ToBox = (100, 0, 60, 40)
        };

        var points = TopologyRouteEngine.FallbackRoute(route);

        Assert.True(points.Count >= 2);
        Assert.Equal(new Point(0, 0), points[0]);
        Assert.Equal(new Point(100, 0), points[^1]);
        // 正交：每段要么水平要么垂直
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            Assert.True(Math.Abs(a.X - b.X) < 0.01 || Math.Abs(a.Y - b.Y) < 0.01);
        }
    }

    [Fact]
    public void BuildRoute_AvoidsObstacle()
    {
        var from = (0.0, 0.0, 60.0, 40.0);
        var to = (300.0, 0.0, 60.0, 40.0);
        var route = TopologyRouteEngine.ComputeRoute("app:a", "app:b", "connectsTo", false, from, to, null);

        var obstacles = new[] { new Rect(150, -60, 40, 180) };
        var points = TopologyRouteEngine.BuildRoute(route, obstacles, fast: false);

        Assert.True(points.Count >= 2);
        Assert.True(TopologyRouteEngine.PathClear(points, obstacles));
    }

    [Fact]
    public void BuildRoute_ClearFallback_MatchesFast()
    {
        var from = (0.0, 0.0, 60.0, 40.0);
        var to = (300.0, 0.0, 60.0, 40.0);
        var route = TopologyRouteEngine.ComputeRoute("app:a", "app:b", "connectsTo", false, from, to, null);

        var fast = TopologyRouteEngine.BuildRoute(route, Array.Empty<Rect>(), fast: true);
        var full = TopologyRouteEngine.BuildRoute(route, Array.Empty<Rect>(), fast: false);

        Assert.Equal(fast, full);
    }

    [Fact]
    public void StyleFor_distinguishes_relation_types()
    {
        var access = TopologyRouteEngine.StyleFor("accessedVia", false);
        Assert.NotNull(access.DashArray);
        Assert.Equal(new[] { 2.0, 2.0 }, access.DashArray!.ToArray());
        Assert.True(access.Thickness < 2);

        var connects = TopologyRouteEngine.StyleFor("connectsTo", false);
        Assert.Null(connects.DashArray);

        var upstream = TopologyRouteEngine.StyleFor("upstreamOf", false);
        Assert.NotNull(upstream.DashArray);
        Assert.NotEqual(access.DashArray, upstream.DashArray);

        Assert.NotNull(TopologyRouteEngine.StyleFor("relatedTo", false).DashArray);
        Assert.NotNull(TopologyRouteEngine.StyleFor("connectsTo", true).DashArray);
    }

    [Fact]
    public void Relation_lookup_resolves_raw_ids_and_default_type()
    {
        var config = new AppConfig
        {
            Applications = new[]
            {
                new ApplicationConfig { Id = "app1", Name = "A" },
                new ApplicationConfig { Id = "app2", Name = "B" }
            },
            Relations = new[]
            {
                new RelationConfig { From = "app1", To = "app2", Type = string.Empty }
            }
        };
        var method = typeof(TopologyViewModel).GetMethod(
            "FindRelationIndex", BindingFlags.NonPublic | BindingFlags.Static)!;

        var index = (int)method.Invoke(null, new object?[] { config, "app:app1", "app:app2", "relatedTo" })!;

        Assert.Equal(0, index);
    }

    [Fact]
    public void DetectHops_FindsSingleCrossing()
    {
        var segments = new List<TopologyRouteEngine.EdgeSegment>
        {
            new(new Point(0, 0), new Point(100, 0), Brushes.SteelBlue, 0),
            new(new Point(50, -50), new Point(50, 50), Brushes.SteelBlue, 1)
        };

        var hops = TopologyRouteEngine.DetectHops(segments);

        Assert.Single(hops);
        Assert.Equal(new Point(50, 0), hops[0].Center);
    }

    [Fact]
    public void Simplify_RemovesCollinearPoints()
    {
        var points = new List<Point>
        {
            new(0, 0), new(50, 0), new(100, 0), new(100, 100)
        };

        var simplified = TopologyRouteEngine.Simplify(points);

        Assert.Equal(3, simplified.Count);
        Assert.Equal(new Point(0, 0), simplified[0]);
        Assert.Equal(new Point(100, 0), simplified[1]);
        Assert.Equal(new Point(100, 100), simplified[2]);
    }
}
