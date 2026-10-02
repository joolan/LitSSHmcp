using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 拓扑连线路由与过桥计算的无状态引擎：
/// 把几何/布局之外的路由、避障、过桥、渲染几何全部集中到这里，便于复用与单元测试。
/// </summary>
public static class TopologyRouteEngine
{
    /// <summary>一条待绘制的边（路由中间产物）。</summary>
    public sealed class EdgeRoute
    {
        public string Type = string.Empty;
        public string From = string.Empty;
        public string To = string.Empty;
        public bool IsDiscovered;
        public bool Horizontal;
        public Point S;
        public Point T;
        public double Channel;
        public double Lateral;
        public string? FixedFromSide;
        public string? FixedToSide;
        public string CorridorKey = string.Empty;
        public double OrderKey;
        public (double X, double Y, double W, double H) FromBox;
        public (double X, double Y, double W, double H) ToBox;
    }

    /// <summary>一条边的已分解线段（用于过桥检测）。</summary>
    public sealed record EdgeSegment(Point A, Point B, Brush Stroke, int Order);

    /// <summary>一处"过桥"（两线交叉）。</summary>
    public sealed class HopInfo
    {
        public Point Center;
        public Vector BottomDir;
        public Vector TopDir;
        public Brush BottomStroke = Brushes.SteelBlue;
        public Brush TopStroke = Brushes.SteelBlue;
    }

    public static EdgeRoute ComputeRoute(
        string from,
        string to,
        string type,
        bool isDiscovered,
        (double X, double Y, double W, double H) fromBox,
        (double X, double Y, double W, double H) toBox,
        EdgeLayout? anchors = null)
    {
        var scx = fromBox.X + fromBox.W / 2;
        var scy = fromBox.Y + fromBox.H / 2;
        var tcx = toBox.X + toBox.W / 2;
        var tcy = toBox.Y + toBox.H / 2;
        var horizontal = Math.Abs(tcx - scx) >= Math.Abs(tcy - scy);

        var route = new EdgeRoute
        {
            Type = type,
            From = from,
            To = to,
            IsDiscovered = isDiscovered,
            Horizontal = horizontal,
            FromBox = fromBox,
            ToBox = toBox,
            FixedFromSide = NormalizeSide(anchors?.From?.Side),
            FixedToSide = NormalizeSide(anchors?.To?.Side)
        };

        if (horizontal)
        {
            route.S = tcx >= scx ? new Point(fromBox.X + fromBox.W, scy) : new Point(fromBox.X, scy);
            route.T = tcx >= scx ? new Point(toBox.X, tcy) : new Point(toBox.X + toBox.W, tcy);
        }
        else
        {
            route.S = tcy >= scy ? new Point(scx, fromBox.Y + fromBox.H) : new Point(scx, fromBox.Y);
            route.T = tcy >= scy ? new Point(tcx, toBox.Y) : new Point(tcx, toBox.Y + toBox.H);
        }

        // 手动锚点优先：固定侧取该边中点，保证移动节点时端点不漂移（快/慢路由一致）
        if (route.FixedFromSide is { } fside)
            route.S = PointOnSide(fromBox, fside);
        if (route.FixedToSide is { } tside)
            route.T = PointOnSide(toBox, tside);

        route.Channel = horizontal ? (route.S.X + route.T.X) / 2 : (route.S.Y + route.T.Y) / 2;
        route.CorridorKey = (horizontal ? "H" : "V") + (int)Math.Round(route.Channel / 60.0);
        route.OrderKey = horizontal ? scy * 1000 + tcy : scx * 1000 + tcx;

        return route;
    }

    /// <summary>
    /// 生成一条边的正交折线。fast 时用 Z 形回退；否则先尝试"无障碍 Z 形"（与拖动预览一致、不跳变），
    /// 被障碍挡住时才用多源多目标 A* 避障。
    /// </summary>
    public static List<Point> BuildRoute(EdgeRoute r, IReadOnlyList<Rect> obstacles, bool fast = false) =>
        BuildRoute(r, new RouteContext(obstacles), fast);

    /// <summary>共享上下文重载：多条边复用同一次构建的障碍索引与坐标压缩。</summary>
    public static List<Point> BuildRoute(EdgeRoute r, RouteContext ctx, bool fast = false)
    {
        if (fast)
            return FallbackRoute(r);

        const double stub = 14;

        var fromRect = new Rect(r.FromBox.X, r.FromBox.Y, r.FromBox.W, r.FromBox.H);
        var toRect = new Rect(r.ToBox.X, r.ToBox.Y, r.ToBox.W, r.ToBox.H);

        // 源/目标自身及其外层包含盒不作为障碍（区块对它的子节点透明）；
        // 排除语义在查询时按候选矩形应用，无需每边过滤分配
        var fallback = FallbackRoute(r);
        if (ctx.PathClear(fallback, fromRect, toRect))
            return fallback;

        var sources = SidePoints(r.FromBox);
        if (r.FixedFromSide is { } fs)
            sources = sources.Where(s => SideOf(s.N) == fs).ToArray();

        var targets = SidePoints(r.ToBox);
        if (r.FixedToSide is { } ts)
            targets = targets.Where(s => SideOf(s.N) == ts).ToArray();

        // 多源多目标：一次 A* 代替 4x4 次
        var sourceSeeds = sources.Select(s => (P: OffsetOnSide(s.P, s.N, r.Lateral, r.FromBox), Stub: OffsetOnSide(s.P, s.N, r.Lateral, r.FromBox) + s.N * stub)).ToArray();
        var targetSeeds = targets.Select(t => (P: t.P, Stub: t.P + t.N * stub)).ToArray();

        var result = AStarMulti(sourceSeeds, targetSeeds, ctx, fromRect, toRect);
        if (result is { } found)
        {
            var routed = new List<Point> { found.FromPoint };
            routed.AddRange(found.Path);
            routed.Add(found.ToPoint);
            return Simplify(routed);
        }

        return FallbackRoute(r);
    }

    /// <summary>固定侧的 Z 形回退路由。</summary>
    public static List<Point> FallbackRoute(EdgeRoute r)
    {
        var points = new List<Point> { r.S };
        if (r.Horizontal)
        {
            var midX = Clamp((r.S.X + r.T.X) / 2, Math.Min(r.S.X, r.T.X) + 6, Math.Max(r.S.X, r.T.X) - 6);
            points.Add(new Point(midX, r.S.Y));
            points.Add(new Point(midX, r.T.Y));
        }
        else
        {
            var midY = Clamp((r.S.Y + r.T.Y) / 2, Math.Min(r.S.Y, r.T.Y) + 6, Math.Max(r.S.Y, r.T.Y) - 6);
            points.Add(new Point(r.S.X, midY));
            points.Add(new Point(r.T.X, midY));
        }

        points.Add(r.T);
        return Simplify(points);
    }

    /// <summary>检测所有线段的交叉处，生成过桥信息（包围盒剪枝 + 排除端点）。</summary>
    public static List<HopInfo> DetectHops(IReadOnlyList<EdgeSegment> segments)
    {
        var hops = new List<HopInfo>();
        for (var i = 0; i < segments.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (segments[i].Order == segments[j].Order)
                    continue;

                var bottom = segments[j];
                var top = segments[i];

                // P2: 轴对齐线段先用包围盒快速剪枝
                if (!BBoxOverlap(bottom.A, bottom.B, top.A, top.B))
                    continue;

                if (!TryIntersect(bottom.A, bottom.B, top.A, top.B, out var p))
                    continue;

                hops.Add(new HopInfo
                {
                    Center = p,
                    BottomDir = bottom.B - bottom.A,
                    TopDir = top.B - top.A,
                    BottomStroke = bottom.Stroke,
                    TopStroke = top.Stroke
                });
            }
        }

        return hops;
    }

    // ---- 渲染几何 ----

    public static Geometry BuildRoundedPolyline(IReadOnlyList<Point> points, double radius)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (var i = 1; i < points.Count - 1; i++)
            {
                var prev = points[i - 1];
                var cur = points[i];
                var next = points[i + 1];
                var v1 = cur - prev;
                var v2 = next - cur;
                if (v1.Length < 0.001 || v2.Length < 0.001)
                {
                    ctx.LineTo(cur, isStroked: true, isSmoothJoin: false);
                    continue;
                }

                var rr = Math.Min(radius, Math.Min(v1.Length / 2, v2.Length / 2));
                v1.Normalize();
                v2.Normalize();
                ctx.LineTo(cur - v1 * rr, isStroked: true, isSmoothJoin: false);
                ctx.QuadraticBezierTo(cur, cur + v2 * rr, isStroked: true, isSmoothJoin: false);
            }

            ctx.LineTo(points[^1], isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    public static Geometry BuildDot(Point center, double radius)
    {
        var g = new EllipseGeometry(center, radius, radius);
        g.Freeze();
        return g;
    }

    public static Geometry BuildArrow(Point tip, Point from, bool discovered)
    {
        var dir = tip - from;
        if (dir.Length < 0.001)
            dir = new Vector(1, 0);
        dir.Normalize();

        var perp = new Vector(-dir.Y, dir.X);
        var length = discovered ? 10 : 13;
        var half = discovered ? 4.5 : 6;
        var back = tip - dir * length;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(tip, isFilled: true, isClosed: true);
            ctx.LineTo(back + perp * half, isStroked: false, isSmoothJoin: false);
            ctx.LineTo(back - perp * half, isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    // 静态缓存：同组合返回同引用，WPF 可跳过描边失效（否则每次重算都强制 Path 重绘）
    private static readonly ConcurrentDictionary<(string Type, bool Discovered), Brush> StrokeCache = new();

    public static Brush StrokeFor(string type, bool discovered) =>
        StrokeCache.GetOrAdd((type, discovered), static key =>
        {
            var color = key.Type == "canAccess"
                ? Color.FromRgb(0x1E, 0x8E, 0x3E)
                : Color.FromRgb(0x0A, 0x66, 0xC2);
            var brush = new SolidColorBrush(color);
            if (key.Discovered)
                brush.Opacity = 0.7;
            brush.Freeze();
            return brush;
        });

    public static (double X, double Y) LabelPoint(IReadOnlyList<Point> points, bool horizontal)
    {
        Point mid;
        if (points.Count >= 3)
            mid = new Point((points[1].X + points[2].X) / 2, (points[1].Y + points[2].Y) / 2);
        else
            mid = new Point((points[0].X + points[^1].X) / 2, (points[0].Y + points[^1].Y) / 2);

        return horizontal ? (mid.X + 6, mid.Y - 8) : (mid.X + 8, mid.Y - 16);
    }

    /// <summary>把折线 + 圆点 + 箭头合并到一条边的渲染（返回三份几何，由视图按绝对坐标叠加）。</summary>
    public static Geometry BuildHopDisc(Point center)
    {
        var g = new EllipseGeometry(center, 6, 6);
        g.Freeze();
        return g;
    }

    // ---- 纯几何（可测试） ----

    public static bool PathClear(IReadOnlyList<Point> points, IReadOnlyList<Rect> obstacles)
    {
        for (var i = 0; i + 1 < points.Count; i++)
        {
            if (!SegmentClear(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, obstacles))
                return false;
        }

        return true;
    }

    public static bool SegmentClear(double x1, double y1, double x2, double y2, IReadOnlyList<Rect> obstacles)
    {
        var horizontal = Math.Abs(y1 - y2) < 0.01;
        var vertical = Math.Abs(x1 - x2) < 0.01;
        if (!horizontal && !vertical)
            return false;

        var loX = Math.Min(x1, x2);
        var hiX = Math.Max(x1, x2);
        var loY = Math.Min(y1, y2);
        var hiY = Math.Max(y1, y2);

        foreach (var rect in obstacles)
        {
            if (horizontal)
            {
                if (y1 > rect.Top && y1 < rect.Bottom && hiX > rect.Left && loX < rect.Right)
                    return false;
            }
            else
            {
                if (x1 > rect.Left && x1 < rect.Right && hiY > rect.Top && loY < rect.Bottom)
                    return false;
            }
        }

        return true;
    }

    public static List<Point> Simplify(IReadOnlyList<Point> points)
    {
        var result = new List<Point>();
        foreach (var p in points)
        {
            if (result.Count > 0 && (Math.Abs(result[^1].X - p.X) < 0.01 && Math.Abs(result[^1].Y - p.Y) < 0.01))
                continue;

            while (result.Count >= 2)
            {
                var a = result[^2];
                var b = result[^1];
                var collinear = (Math.Abs(a.X - b.X) < 0.01 && Math.Abs(b.X - p.X) < 0.01) ||
                                (Math.Abs(a.Y - b.Y) < 0.01 && Math.Abs(b.Y - p.Y) < 0.01);
                if (!collinear)
                    break;
                result.RemoveAt(result.Count - 1);
            }

            result.Add(p);
        }

        return result;
    }

    public static bool TryIntersect(Point a, Point b, Point c, Point d, out Point point)
    {
        point = default;
        var r = b - a;
        var s = d - c;
        var denom = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(denom) < 1e-6)
            return false; // 平行/共线

        var ac = c - a;
        var t = (ac.X * s.Y - ac.Y * s.X) / denom;
        var u = (ac.X * r.Y - ac.Y * r.X) / denom;
        if (t <= 0.02 || t >= 0.98 || u <= 0.02 || u >= 0.98)
            return false; // 端点/顶点附近不处理

        point = a + r * t;
        return true;
    }

    public static Geometry BuildShortLine(Point center, Vector dir)
    {
        if (dir.Length < 0.001)
            dir = new Vector(1, 0);
        dir.Normalize();

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(center - dir * 6, isFilled: false, isClosed: false);
            ctx.LineTo(center + dir * 6, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    public static Geometry BuildHopArc(Point center, Vector dir)
    {
        if (dir.Length < 0.001)
            dir = new Vector(1, 0);
        dir.Normalize();

        var perp = new Vector(-dir.Y, dir.X);
        var a = center - dir * 5;
        var b = center + dir * 5;
        var ctrl = center + perp * 8;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(a, isFilled: false, isClosed: false);
            ctx.QuadraticBezierTo(ctrl, b, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnSide((double X, double Y, double W, double H) box, string side) => side switch
    {
        "left" => new Point(box.X, box.Y + box.H / 2),
        "right" => new Point(box.X + box.W, box.Y + box.H / 2),
        "top" => new Point(box.X + box.W / 2, box.Y),
        _ => new Point(box.X + box.W / 2, box.Y + box.H)
    };

    private static (Point P, Vector N)[] SidePoints((double X, double Y, double W, double H) box)
    {
        var cx = box.X + box.W / 2;
        var cy = box.Y + box.H / 2;
        return new[]
        {
            (new Point(box.X, cy), new Vector(-1, 0)),
            (new Point(box.X + box.W, cy), new Vector(1, 0)),
            (new Point(cx, box.Y), new Vector(0, -1)),
            (new Point(cx, box.Y + box.H), new Vector(0, 1))
        };
    }

    private static string? NormalizeSide(string? side) =>
        string.IsNullOrWhiteSpace(side) || side.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : side.Trim().ToLowerInvariant();

    private static string SideOf(Vector normal) =>
        normal.X < 0 ? "left" : normal.X > 0 ? "right" : normal.Y < 0 ? "top" : "bottom";

    private static Point OffsetOnSide(Point point, Vector normal, double delta, (double X, double Y, double W, double H) box)
    {
        var along = new Vector(-normal.Y, normal.X);
        var moved = point + along * delta;
        return new Point(
            Clamp(moved.X, box.X + 8, box.X + box.W - 8),
            Clamp(moved.Y, box.Y + 8, box.Y + box.H - 8));
    }

    private static double PathLength(IReadOnlyList<Point> points)
    {
        var total = 0.0;
        for (var i = 1; i < points.Count; i++)
            total += (points[i] - points[i - 1]).Length;
        return total;
    }

    private static bool BBoxOverlap(Point a, Point b, Point c, Point d)
    {
        var minX1 = Math.Min(a.X, b.X);
        var maxX1 = Math.Max(a.X, b.X);
        var minY1 = Math.Min(a.Y, b.Y);
        var maxY1 = Math.Max(a.Y, b.Y);
        var minX2 = Math.Min(c.X, d.X);
        var maxX2 = Math.Max(c.X, d.X);
        var minY2 = Math.Min(c.Y, d.Y);
        var maxY2 = Math.Max(c.Y, d.Y);

        return minX1 <= maxX2 && maxX1 >= minX2 && minY1 <= maxY2 && maxY1 >= minY2;
    }

    private static double Clamp(double value, double min, double max) =>
        max < min ? (min + max) / 2 : Math.Min(Math.Max(value, min), max);

    // ---- P3: 多源多目标 A* + P1: 障碍空间索引 + P2: 共享路由上下文 ----

    /// <summary>
    /// 一次构建、多条边共享的路由上下文：障碍网格索引与排序坐标只建一次，
    /// 端点盒容器排除改为查询期按候选应用，消除每边的过滤分配与索引重建。
    /// </summary>
    public sealed class RouteContext
    {
        private readonly ObstacleIndex _index;
        private readonly double[] _xs;
        private readonly double[] _ys;

        public RouteContext(IReadOnlyList<Rect> obstacles)
        {
            _index = new ObstacleIndex(obstacles);
            var xs = new SortedSet<double>();
            var ys = new SortedSet<double>();
            foreach (var rect in obstacles)
            {
                xs.Add(rect.Left);
                xs.Add(rect.Right);
                ys.Add(rect.Top);
                ys.Add(rect.Bottom);
            }
            _xs = xs.ToArray();
            _ys = ys.ToArray();
        }

        internal double[] ObsX => _xs;
        internal double[] ObsY => _ys;

        /// <summary>网格索引查询（含端点盒容器排除：候选矩形包含任一端点盒则不阻挡）。</summary>
        public bool SegmentClear(double x1, double y1, double x2, double y2, Rect fromRect, Rect toRect) =>
            _index.SegmentClear(x1, y1, x2, y2, fromRect, toRect);

        /// <summary>整条路径可达性检查（替代"先过滤障碍列表再暴力扫描"）。</summary>
        public bool PathClear(IReadOnlyList<Point> points, Rect fromRect, Rect toRect)
        {
            for (var i = 0; i + 1 < points.Count; i++)
            {
                if (!_index.SegmentClear(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, fromRect, toRect))
                    return false;
            }
            return true;
        }
    }

    /// <summary>两个升序数组归并去重（容差 1e-9，同值时优先保留 stub 坐标以保证种子精确匹配）。</summary>
    private static double[] MergeSorted(double[] a, double[] b)
    {
        if (b.Length == 0)
            return a;
        var merged = new double[a.Length + b.Length];
        var i = 0;
        var j = 0;
        var k = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] < b[j] - 1e-9)
                merged[k++] = a[i++];
            else if (b[j] < a[i] - 1e-9)
                merged[k++] = b[j++];
            else
            {
                merged[k++] = b[j++];
                i++;
            }
        }
        while (i < a.Length) merged[k++] = a[i++];
        while (j < b.Length) merged[k++] = b[j++];
        if (k != merged.Length)
            Array.Resize(ref merged, k);
        return merged;
    }

    private static (Point FromPoint, Point ToPoint, List<Point> Path)? AStarMulti(
        IReadOnlyList<(Point P, Point Stub)> sources,
        IReadOnlyList<(Point P, Point Stub)> targets,
        RouteContext ctx,
        Rect fromRect,
        Rect toRect)
    {
        // 共享障碍坐标 + 本边 stub 坐标（每边 ≤8 个）归并，替代每边重建 SortedSet
        var stubXs = new SortedSet<double>();
        var stubYs = new SortedSet<double>();
        foreach (var (_, stub) in sources) { stubXs.Add(stub.X); stubYs.Add(stub.Y); }
        foreach (var (_, stub) in targets) { stubXs.Add(stub.X); stubYs.Add(stub.Y); }

        var xa = MergeSorted(ctx.ObsX, stubXs.ToArray());
        var ya = MergeSorted(ctx.ObsY, stubYs.ToArray());
        var nx = xa.Length;
        var ny = ya.Length;
        if (nx == 0 || ny == 0)
            return null;

        var index = ctx;

        int Idx(int i, int j) => i * ny + j;

        var dist = new double[nx * ny];
        var prev = new int[nx * ny];
        var visited = new bool[nx * ny];
        var sourceOrigin = new Point[nx * ny];
        var hasOrigin = new bool[nx * ny];
        Array.Fill(dist, double.PositiveInfinity);
        Array.Fill(prev, -1);

        foreach (var (p, stub) in sources)
        {
            var si = Array.IndexOf(xa, stub.X);
            var sj = Array.IndexOf(ya, stub.Y);
            if (si < 0 || sj < 0)
                continue;
            var idx = Idx(si, sj);
            var c = (stub - p).Length;
            if (c < dist[idx])
            {
                dist[idx] = c;
                prev[idx] = -1;
                sourceOrigin[idx] = p;
                hasOrigin[idx] = true;
            }
        }

        var targetIdx = new HashSet<int>();
        foreach (var (_, stub) in targets)
        {
            var ti = Array.IndexOf(xa, stub.X);
            var tj = Array.IndexOf(ya, stub.Y);
            if (ti >= 0 && tj >= 0)
                targetIdx.Add(Idx(ti, tj));
        }

        double Heuristic(int node)
        {
            var ci = node / ny;
            var cj = node % ny;
            var best = double.PositiveInfinity;
            foreach (var (_, stub) in targets)
                best = Math.Min(best, Math.Abs(xa[ci] - stub.X) + Math.Abs(ya[cj] - stub.Y));
            return best;
        }

        var queue = new PriorityQueue<int, double>();
        for (var n = 0; n < dist.Length; n++)
        {
            if (!double.IsInfinity(dist[n]))
                queue.Enqueue(n, dist[n] + Heuristic(n));
        }

        var bestGoal = -1;
        var bestGoalCost = double.PositiveInfinity;

        while (queue.TryDequeue(out var current, out _))
        {
            if (visited[current])
                continue;
            visited[current] = true;

            if (targetIdx.Contains(current) && dist[current] < bestGoalCost)
            {
                bestGoalCost = dist[current];
                bestGoal = current;
            }

            var ci = current / ny;
            var cj = current % ny;
            foreach (var (ni, nj) in new[] { (ci - 1, cj), (ci + 1, cj), (ci, cj - 1), (ci, cj + 1) })
            {
                if (ni < 0 || nj < 0 || ni >= nx || nj >= ny)
                    continue;
                if (!index.SegmentClear(xa[ci], ya[cj], xa[ni], ya[nj], fromRect, toRect))
                    continue;

                var next = Idx(ni, nj);
                var candidate = dist[current] + Math.Abs(xa[ni] - xa[ci]) + Math.Abs(ya[nj] - ya[cj]);
                if (candidate >= dist[next])
                    continue;

                dist[next] = candidate;
                prev[next] = current;
                queue.Enqueue(next, candidate + Heuristic(next));
            }
        }

        if (bestGoal < 0)
            return null;

        var nodes = new List<int>();
        for (var n = bestGoal; n != -1; n = prev[n])
            nodes.Add(n);
        nodes.Reverse();

        var path = new List<Point>();
        foreach (var n in nodes)
            path.Add(new Point(xa[n / ny], ya[n % ny]));

        var fromPoint = hasOrigin[nodes[0]] ? sourceOrigin[nodes[0]] : path[0];
        var goalPoint = new Point(xa[bestGoal / ny], ya[bestGoal % ny]);
        var toPoint = goalPoint;
        foreach (var (p, stub) in targets)
        {
            if (Math.Abs(stub.X - goalPoint.X) < 0.01 && Math.Abs(stub.Y - goalPoint.Y) < 0.01)
            {
                toPoint = p;
                break;
            }
        }

        return (fromPoint, toPoint, path);
    }

    /// <summary>P1：把障碍矩形放入均匀网格，加速"线段是否穿过障碍"的查询。</summary>
    private sealed class ObstacleIndex
    {
        private const double Cell = 64;
        private readonly Dictionary<(int X, int Y), List<Rect>> _grid = new();

        public ObstacleIndex(IReadOnlyList<Rect> obstacles)
        {
            foreach (var r in obstacles)
                Add(r);
        }

        private void Add(Rect r)
        {
            var x0 = (int)Math.Floor(r.Left / Cell);
            var x1 = (int)Math.Floor(r.Right / Cell);
            var y0 = (int)Math.Floor(r.Top / Cell);
            var y1 = (int)Math.Floor(r.Bottom / Cell);
            for (var x = x0; x <= x1; x++)
            {
                for (var y = y0; y <= y1; y++)
                {
                    if (!_grid.TryGetValue((x, y), out var list))
                        _grid[(x, y)] = list = new List<Rect>();
                    list.Add(r);
                }
            }
        }

        public bool SegmentClear(double x1, double y1, double x2, double y2) =>
            SegmentClear(x1, y1, x2, y2, Rect.Empty, Rect.Empty);

        /// <summary>
        /// 线段是否不与任何障碍相交（仅轴对齐）。
        /// 候选矩形若完全包含 <paramref name="excludeFrom"/> 或 <paramref name="excludeTo"/>（源/目标
        /// 自身及其外层容器），则视为透明，不阻挡——等价于旧版"先过滤障碍列表"。
        /// </summary>
        public bool SegmentClear(double x1, double y1, double x2, double y2, Rect excludeFrom, Rect excludeTo)
        {
            var horizontal = Math.Abs(y1 - y2) < 0.01;
            var vertical = Math.Abs(x1 - x2) < 0.01;
            if (!horizontal && !vertical)
                return false;

            var loX = Math.Min(x1, x2);
            var hiX = Math.Max(x1, x2);
            var loY = Math.Min(y1, y2);
            var hiY = Math.Max(y1, y2);

            if (horizontal)
            {
                var y = (int)Math.Floor(y1 / Cell);
                var xlo = (int)Math.Floor(loX / Cell);
                var xhi = (int)Math.Floor(hiX / Cell);
                for (var x = xlo; x <= xhi; x++)
                {
                    if (!_grid.TryGetValue((x, y), out var rects))
                        continue;
                    foreach (var rect in rects)
                    {
                        if (rect.Contains(excludeFrom) || rect.Contains(excludeTo))
                            continue;
                        if (y1 > rect.Top && y1 < rect.Bottom && hiX > rect.Left && loX < rect.Right)
                            return false;
                    }
                }
            }
            else
            {
                var x = (int)Math.Floor(x1 / Cell);
                var ylo = (int)Math.Floor(loY / Cell);
                var yhi = (int)Math.Floor(hiY / Cell);
                for (var y = ylo; y <= yhi; y++)
                {
                    if (!_grid.TryGetValue((x, y), out var rects))
                        continue;
                    foreach (var rect in rects)
                    {
                        if (rect.Contains(excludeFrom) || rect.Contains(excludeTo))
                            continue;
                        if (x1 > rect.Left && x1 < rect.Right && hiY > rect.Top && loY < rect.Bottom)
                            return false;
                    }
                }
            }

            return true;
        }
    }
}
