using System.Windows;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

/// <summary>资产拓扑的纯几何/影响面计算：不依赖 ViewModel 状态，便于单测与复用。</summary>
public static class TopologyGeometry
{
    public const double PortRadius = 5;
    public const double PortGap = 3;
    public const double EdgeBand = 6;
    public const double EdgeCornerGap = 8;
    public const double ResizeMinW = 80;
    public const double ResizeMinH = 36;

    public static (HashSet<string> Nodes, HashSet<string> EdgeKeys) ComputeImpact(
        string rootId, IEnumerable<TopologyEdge> edges, ISet<string> visible, int maxHops = 2)
    {
        var incoming = edges
            .Where(e => visible.Contains(e.From) && visible.Contains(e.To))
            .GroupBy(e => e.To, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var impacted = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var impactedEdges = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Id, int Hops)>();
        queue.Enqueue((rootId, 0));

        while (queue.Count > 0)
        {
            var (current, hops) = queue.Dequeue();
            if (hops >= maxHops || !incoming.TryGetValue(current, out var parents))
                continue;

            foreach (var edge in parents)
            {
                impactedEdges.Add($"{edge.From}|{edge.Type}|{edge.To}");
                if (!impacted.Add(edge.From))
                    continue;
                queue.Enqueue((edge.From, hops + 1));
            }
        }

        return (impacted, impactedEdges);
    }

    /// <summary>端口圆心：在节点对应边的外侧（圆半径 + 间隙），与边缩放带互不重叠。</summary>
    public static Point PortCenterFor(Rect r, string side)
    {
        var off = PortRadius + PortGap;
        return side switch
        {
            "left" => new Point(r.X - off, r.Y + r.Height / 2),
            "right" => new Point(r.X + r.Width + off, r.Y + r.Height / 2),
            "top" => new Point(r.X + r.Width / 2, r.Y - off),
            _ => new Point(r.X + r.Width / 2, r.Y + r.Height + off)
        };
    }

    /// <summary>四边缩放带命中：带在节点边内侧 6px、两端各避开角手柄 8px（角手柄优先）。</summary>
    public static string? EdgeHandleAt(Rect sel, Point p)
    {
        var innerL = sel.X + EdgeCornerGap;
        var innerT = sel.Y + EdgeCornerGap;
        var innerR = sel.Right - EdgeCornerGap;
        var innerB = sel.Bottom - EdgeCornerGap;
        if (innerL >= innerR || innerT >= innerB)
            return null;

        var inX = p.X >= innerL && p.X <= innerR;
        var inY = p.Y >= innerT && p.Y <= innerB;
        if (inX && p.Y >= sel.Y && p.Y <= sel.Y + EdgeBand) return "n";
        if (inX && p.Y >= sel.Bottom - EdgeBand && p.Y <= sel.Bottom) return "s";
        if (inY && p.X >= sel.X && p.X <= sel.X + EdgeBand) return "w";
        if (inY && p.X >= sel.Right - EdgeBand && p.X <= sel.Right) return "e";
        return null;
    }

    /// <summary>结果只取决于 (start, total)，与事件路径无关；来回拖动不漂移并吸附 10px 网格。</summary>
    public static (double X, double Y, double W, double H) ComputeResize(
        string handle, Rect start, double tx, double ty,
        double minW = ResizeMinW, double minH = ResizeMinH)
    {
        var right = start.X + start.Width;
        var bottom = start.Y + start.Height;
        var x = start.X;
        var y = start.Y;
        var w = start.Width;
        var h = start.Height;

        if (handle.Contains('e')) w = Math.Max(minW, start.Width + tx);
        if (handle.Contains('s')) h = Math.Max(minH, start.Height + ty);
        if (handle.Contains('w')) { w = Math.Max(minW, start.Width - tx); x = right - w; }
        if (handle.Contains('n')) { h = Math.Max(minH, start.Height - ty); y = bottom - h; }

        x = Math.Round(x / 10.0) * 10.0;
        y = Math.Round(y / 10.0) * 10.0;
        w = Math.Max(minW, Math.Round(w / 10.0) * 10.0);
        h = Math.Max(minH, Math.Round(h / 10.0) * 10.0);
        return (x, y, w, h);
    }

    /// <summary>把子节点钳制在父容器内（容器过小时按中心放置，不产生反向钳制）。</summary>
    public static Rect ClampChildTo(Rect parent, Rect child, double header)
    {
        var x = Clamp(child.X, parent.X + 6, parent.X + parent.Width - child.Width - 6);
        var y = Clamp(child.Y, parent.Y + header - 6, parent.Y + parent.Height - child.Height - 6);
        return new Rect(x, y, child.Width, child.Height);
    }

    private static double Clamp(double value, double min, double max) =>
        min > max ? (min + max) / 2 : Math.Clamp(value, min, max);
}
