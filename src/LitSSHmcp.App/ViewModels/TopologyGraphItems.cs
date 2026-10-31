using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace LitSSHmcp.App.ViewModels;

public abstract class GraphItemVm : INotifyPropertyChanged
{
    private double _x;
    private double _y;
    private double _width;
    private double _height;

    public string Id { get; set; } = string.Empty;
    public string Tooltip { get; set; } = string.Empty;

    /// <summary>证书等资产已过期：红色高亮。</summary>
    public bool IsExpired { get; set; }

    /// <summary>证书等资产即将过期（30 天内）：橙色高亮。</summary>
    public bool IsExpiringSoon { get; set; }

    private bool _isDimmed;

    /// <summary>选中节点后，非影响面节点降低透明度。</summary>
    public bool IsDimmed { get => _isDimmed; set => Set(ref _isDimmed, value); }

    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public double Width { get => _width; set => Set(ref _width, value); }
    public double Height { get => _height; set => Set(ref _height, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class GraphBoxVm : GraphItemVm
{
    public string Label { get; set; } = string.Empty;
    public string SubLabel { get; set; } = string.Empty;

    /// <summary>服务器已禁用：不可建链、不可连接，图上以灰化 + "已禁用"角标提示。</summary>
    public bool IsDisabled { get; set; }
}

public class GraphChipVm : GraphItemVm
{
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public class GraphNodeVm : GraphItemVm
{
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public class GraphEdgeVm : INotifyPropertyChanged
{
    private Geometry _line = Geometry.Empty;
    private Geometry _dot = Geometry.Empty;
    private Geometry _arrow = Geometry.Empty;
    private Brush _stroke = Brushes.SteelBlue;
    private DoubleCollection? _dashArray;
    private double _thickness = 2;
    private string _label = string.Empty;
    private double _labelX;
    private double _labelY;
    private bool _isDiscovered;
    private bool _isDerived;
    private bool _isDisputed;
    private bool _isDimmed;

    /// <summary>正交折线路径（拐角圆角化）。</summary>
    public Geometry Line { get => _line; set => Set(ref _line, value); }

    /// <summary>起点圆点。</summary>
    public Geometry Dot { get => _dot; set => Set(ref _dot, value); }

    /// <summary>终点箭头。</summary>
    public Geometry Arrow { get => _arrow; set => Set(ref _arrow, value); }

    /// <summary>线条/圆点/箭头颜色（按关系类型，静态缓存同引用）。</summary>
    public Brush Stroke { get => _stroke; set => Set(ref _stroke, value); }

    /// <summary>关系类型对应的虚线样式（null=实线）。</summary>
    public DoubleCollection? DashArray { get => _dashArray; set => Set(ref _dashArray, value); }

    /// <summary>关系类型对应的线宽。</summary>
    public double Thickness { get => _thickness; set => Set(ref _thickness, value); }

    public string Type { get; set; } = string.Empty;
    public string Label { get => _label; set => Set(ref _label, value); }
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;

    /// <summary>关系证据（自动发现时说明来源：ESTAB 连接 / mysql processlist 客户端 / 配置扫描文件等）。</summary>
    public string Evidence { get; set; } = string.Empty;

    /// <summary>冲突原因（自动检测写入）。</summary>
    public string? DisputeReason { get; set; }

    /// <summary>布局锚点键（From|Type|To，建边时算一次；拖动热路径免字符串分配）。</summary>
    public string Key { get; set; } = string.Empty;
    public List<Point> Points { get; set; } = new();
    public double LabelX { get => _labelX; set => Set(ref _labelX, value); }
    public double LabelY { get => _labelY; set => Set(ref _labelY, value); }
    public bool IsDiscovered { get => _isDiscovered; set => Set(ref _isDiscovered, value); }

    /// <summary>由运行环境/跳板/隧道配置推导的关系（不是人工 relation，不能在此删除）。</summary>
    public bool IsDerived { get => _isDerived; set => Set(ref _isDerived, value); }

    /// <summary>人工配置与自动发现不一致：红色显示，确认后清除。</summary>
    public bool IsDisputed { get => _isDisputed; set => Set(ref _isDisputed, value); }

    /// <summary>选中节点后，不在影响面路径上的边降低透明度。</summary>
    public bool IsDimmed { get => _isDimmed; set => Set(ref _isDimmed, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>交叉"过桥"：白色遮罩 + 下层直线补段 + 上层拱形跳线。</summary>
public class GraphHopVm
{
    public Geometry Disc { get; set; } = Geometry.Empty;
    public Geometry Bridge { get; set; } = Geometry.Empty;
    public Geometry Arc { get; set; } = Geometry.Empty;
    public Brush BridgeStroke { get; set; } = Brushes.SteelBlue;
    public Brush ArcStroke { get; set; } = Brushes.SteelBlue;
}

/// <summary>缩放手柄（选中节点时显示）。INPC 以支持就地更新（避免每次 mousemove 重建容器）。</summary>
public class GraphHandleVm : INotifyPropertyChanged
{
    private Geometry _shape = Geometry.Empty;
    private Rect _bounds;

    public Geometry Shape { get => _shape; set => Set(ref _shape, value); }
    public string Tag { get; set; } = string.Empty;
    public Rect Bounds { get => _bounds; set => Set(ref _bounds, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
