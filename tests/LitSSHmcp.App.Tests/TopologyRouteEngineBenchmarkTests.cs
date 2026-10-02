using System.Diagnostics;
using System.Windows;
using LitSSHmcp.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace LitSSHmcp.App.Tests;

public class TopologyRouteEngineBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public TopologyRouteEngineBenchmarkTests(ITestOutputHelper output) => _output = output;

    /// <summary>合成 200 障碍 + 300 路由的引擎基准（耗时仅输出，不断言阈值）。</summary>
    [Fact]
    public void BuildRoute_300Routes_Timing()
    {
        var obstacles = new List<Rect>();
        for (var i = 0; i < 200; i++)
        {
            var x = (i % 20) * 80.0;
            var y = (i / 20) * 80.0;
            obstacles.Add(new Rect(x + 20, y + 20, 40, 40));
        }

        var boxes = new List<(double X, double Y, double W, double H)>();
        for (var i = 0; i < 300; i++)
        {
            var x = ((i * 37) % 20) * 80.0;
            var y = ((i * 17) % 20) * 80.0;
            boxes.Add((x, y, 60, 40));
        }

        var routes = new List<TopologyRouteEngine.EdgeRoute>();
        for (var i = 0; i < 300; i++)
        {
            var toIdx = (i * 7 + 3) % 300;
            if (toIdx == i)
                toIdx = (toIdx + 1) % 300;
            routes.Add(TopologyRouteEngine.ComputeRoute($"a{i}", $"b{i}", "connectsTo", false, boxes[i], boxes[toIdx], null));
        }

        // 预热
        foreach (var r in routes)
            TopologyRouteEngine.BuildRoute(r, obstacles, fast: false);

        var sw = Stopwatch.StartNew();
        foreach (var r in routes)
            TopologyRouteEngine.BuildRoute(r, obstacles, fast: false);
        sw.Stop();
        _output.WriteLine($"BuildRoute(per-call): {sw.Elapsed.TotalMilliseconds:F1} ms / {routes.Count} routes");

        // P2: 共享上下文（障碍索引与坐标压缩只建一次）
        var ctx = new TopologyRouteEngine.RouteContext(obstacles);
        foreach (var r in routes)
            TopologyRouteEngine.BuildRoute(r, ctx, fast: false);

        sw.Restart();
        foreach (var r in routes)
            TopologyRouteEngine.BuildRoute(r, ctx, fast: false);
        sw.Stop();
        _output.WriteLine($"BuildRoute(shared ctx): {sw.Elapsed.TotalMilliseconds:F1} ms / {routes.Count} routes");

        for (var i = 0; i < routes.Count; i++)
        {
            Assert.Equal(
                TopologyRouteEngine.BuildRoute(routes[i], obstacles, fast: false),
                TopologyRouteEngine.BuildRoute(routes[i], ctx, fast: false));
        }

        var clear = 0;
        foreach (var r in routes)
        {
            var points = TopologyRouteEngine.BuildRoute(r, obstacles, fast: false);
            Assert.True(points.Count >= 2);
            if (TopologyRouteEngine.PathClear(points, obstacles))
                clear++;
        }
        _output.WriteLine($"clear: {clear}/{routes.Count}");
    }
}
