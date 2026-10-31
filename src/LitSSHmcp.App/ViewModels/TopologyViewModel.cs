using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.ViewModels;

public partial class TopologyViewModel : INotifyPropertyChanged, IConfigReloadable
{
    private const double ServerX = 20;
    private const double ServerWidth = 320;
    private const double ServerHeader = 36;
    private const double ChipHeight = 30;
    private const double ChipGap = 6;
    private const double StandaloneAppX = 390;
    private const double StandaloneAppWidth = 220;
    private const double StandaloneAppHeight = 48;
    private const double DsX = 700;
    private const double DsWidth = 200;
    private const double DsHeight = 48;
    private const double LaneGap = 0;

    private readonly IAppServices _services;
    private readonly IConfigService _configService;
    private readonly ITopologyService _topology;
    private readonly TopologyLayoutHistory _layoutHistory = new(new TopologyLayoutStore());
    private TopologyGraph? _graph;
    private readonly Dictionary<string, (double X, double Y, double W, double H)> _geometry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parentOf = new(StringComparer.Ordinal);

    // 每帧热路径索引（只在 ApplyGraph 全量重建，拖动/缩放期间零分配查询）
    private GraphItemVm[] _allItems = Array.Empty<GraphItemVm>();
    private readonly Dictionary<string, GraphItemVm> _itemsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _childrenOf = new(StringComparer.Ordinal);
    private string? _selectedId;
    private (string Id, string Side)? _linkFrom;
    private GraphEdgeVm? _selectedEdge;
    private string _selEdgeOldType = string.Empty;
    private string _selEdgeType = "runsOn";
    private string _selEdgeNote = string.Empty;
    private string _selEdgeHint = string.Empty;
    private Geometry _linkGeometry = Geometry.Empty;
    private Geometry _selectedEdgeLine = Geometry.Empty;
    private string _statusMessage = string.Empty;
    private double _canvasWidth = 2000;
    private double _canvasHeight = 1400;
    private double _selX = 0;
    private double _selY = 0;
    private double _selW = 0;
    private double _selH = 0;
    private Dictionary<string, (double X, double Y, double W, double H)>? _dragSnapshot;
    private Rect? _dragStartRect;

    /// <summary>拖动/缩放起点的鼠标锚点（GraphCanvas 坐标）。位置始终由「快照起点 + 相对锚点总位移」
    /// 推导，与事件路径无关 —— 彻底消除增量舍入造成的累积漂移（来回移动后节点跟不上鼠标）。</summary>
    private Point? _dragAnchor;
    private bool _synthetic;
    private int _frameCount;
    private double _frameTotalMs;
    private double _frameMaxMs;

    // 手动布局缓存：避免每次重算连线都读盘/反序列化 topology-layout.json
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastFastMs;
    private bool _fastPending;
    private string? _fastNodeId;
    private DispatcherTimer? _fastTimer;
    private const int FastIntervalMs = 25;
    private readonly IWindowService _windows;

    public TopologyViewModel(IWindowService? windows = null, IAppServices? services = null)
    {
        _services = services ?? new AppServices(windows: windows);
        _windows = _services.Windows;
        _configService = _services.Config;
        _topology = _services.CreateTopologyService();
        Load();
    }

    /// <summary>一次图谱加载/重算完成（ApplyGraph 结束）后触发，供视图做"适应窗口"等操作。</summary>
    public event Action? GraphLoaded;

    /// <summary>当前是否已有可显示的内容（用于判断"适应"是否该执行）。</summary>
    public bool HasContent => Boxes.Count + Chips.Count + Nodes.Count > 0;

    public ObservableCollection<GraphBoxVm> Boxes { get; } = new();
    public ObservableCollection<GraphChipVm> Chips { get; } = new();
    public ObservableCollection<GraphNodeVm> Nodes { get; } = new();
    public ObservableCollection<GraphEdgeVm> Edges { get; } = new();
    public ObservableCollection<GraphHopVm> Hops { get; } = new();
    public ObservableCollection<GraphEdgeVm> Labels { get; } = new();
    public ObservableCollection<GraphHandleVm> Handles { get; } = new();
    public ObservableCollection<GraphHandleVm> Ports { get; } = new();
    public ObservableCollection<GraphHandleVm> EdgeAnchors { get; } = new();

    public double CanvasWidth
    {
        get => _canvasWidth;
        set { _canvasWidth = value; OnPropertyChanged(); }
    }

    public double CanvasHeight
    {
        get => _canvasHeight;
        set { _canvasHeight = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private string _impactSummary = string.Empty;

    /// <summary>选中节点后的反向影响面摘要（谁依赖它）。</summary>
    public string ImpactSummary
    {
        get => _impactSummary;
        private set { _impactSummary = value; OnPropertyChanged(); }
    }

    public void ReloadConfig() => Load();

    public void Load() => AsyncOperation.Run("加载拓扑图", async () =>
    {
        try
        {
            var graph = await _topology.GetGraphAsync();
            _synthetic = false;
            ApplyGraph(graph);
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载拓扑失败: {ex.Message}";
        }
    });

    private void ApplyGraph(TopologyGraph graph)
    {
        using var perf = PerfLog.Scope("Load", () => $"nodes={graph.Nodes.Length} edges={graph.Edges.Length}");
        try
        {
            _graph = graph;
            ClearSelection();
            ClearEdgeSelection();

            Boxes.Clear();
            Chips.Clear();
            Nodes.Clear();
            Edges.Clear();
            Hops.Clear();
            _parentOf.Clear();

            var geometry = new Dictionary<string, (double X, double Y, double W, double H)>(StringComparer.Ordinal);
            var labelById = new Dictionary<string, string>(StringComparer.Ordinal);

            var visibleNodes = graph.Nodes;
            var servers = visibleNodes.Where(n => n.Type == "ssh").ToArray();
            var apps = visibleNodes.Where(n => n.Type == "application").ToArray();
            var datasources = visibleNodes.Where(n => n.Type != "ssh" && n.Type != "application").ToArray();

            foreach (var node in graph.Nodes)
                labelById[node.Id] = LabelWithPort(node);
            var nodesById = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

            var tooltipById = graph.Nodes.ToDictionary(n => n.Id, BuildTooltip);

            // runsOn: 被托管节点 -> 服务器（应用与数据库均可 runsOn）
            var hostedBy = graph.Edges
                .Where(e => e.Type == "runsOn")
                .GroupBy(e => e.From)
                .ToDictionary(g => g.Key, g => g.First().To);

            // runsIn: 应用/数据源 -> Docker 应用（旧画布用缩进表达第二级）
            var dockerMembers = graph.Edges
                .Where(e => e.Type == "runsIn")
                .GroupBy(e => e.To)
                .ToDictionary(g => g.Key, g => g.Select(e => e.From).Distinct(StringComparer.Ordinal).ToArray());
            var memberToDocker = dockerMembers
                .SelectMany(kv => kv.Value.Select(member => (Member: member, Docker: kv.Key)))
                .GroupBy(x => x.Member)
                .ToDictionary(g => g.Key, g => g.First().Docker, StringComparer.Ordinal);

            var hostedApps = new Dictionary<string, List<TopologyNode>>(StringComparer.Ordinal);
            var hostedDs = new Dictionary<string, List<TopologyNode>>(StringComparer.Ordinal);
            var standaloneApps = new List<TopologyNode>();
            var standaloneDs = new List<TopologyNode>();

            foreach (var app in apps)
            {
                if (hostedBy.TryGetValue(app.Id, out var sid) && servers.Any(s => s.Id == sid))
                    Add(hostedApps, sid, app);
                else if (!IsNestedUnderHostedDocker(app.Id, memberToDocker, hostedBy, servers))
                    standaloneApps.Add(app);
            }

            foreach (var ds in datasources)
            {
                if (hostedBy.TryGetValue(ds.Id, out var sid) && servers.Any(s => s.Id == sid))
                    Add(hostedDs, sid, ds);
                else if (!IsNestedUnderHostedDocker(ds.Id, memberToDocker, hostedBy, servers))
                    standaloneDs.Add(ds);
            }

            // 左列：服务器区块（内嵌托管的 应用/数据库；Docker 成员用缩进表达两级）
            double serverY = 20;
            foreach (var server in servers)
            {
                var appsHere = hostedApps.TryGetValue(server.Id, out var a) ? a : new List<TopologyNode>();
                var dsHere = hostedDs.TryGetValue(server.Id, out var d) ? d : new List<TopologyNode>();
                var directChildren = appsHere.Concat(dsHere).ToArray();
                var children = new List<(TopologyNode Node, int Indent)>();
                foreach (var child in directChildren)
                {
                    children.Add((child, 0));
                    if (!dockerMembers.TryGetValue(child.Id, out var members))
                        continue;

                    foreach (var memberId in members)
                    {
                        if (!nodesById.TryGetValue(memberId, out var member) ||
                            children.Any(x => x.Node.Id == memberId))
                            continue;
                        children.Add((member, 1));
                    }
                }

                var chipArea = children.Count == 0 ? 24 : children.Count * ChipHeight + (children.Count - 1) * ChipGap;
                var boxHeight = ServerHeader + chipArea + 12;

                var sub = directChildren.Length == 0
                    ? "(无托管资产)"
                    : $"{appsHere.Count} 应用 / {dsHere.Count} 数据库 (runsOn)";

                if (InfoInt(server, "accessProfileCount") is int accessCount && accessCount > 1)
                    sub += $" · {accessCount} 接入方式";

                Boxes.Add(new GraphBoxVm
                {
                    Id = server.Id,
                    Label = labelById[server.Id],
                    SubLabel = sub,
                    Tooltip = tooltipById[server.Id],
                    IsDisabled = IsNodeDisabled(server),
                    X = ServerX,
                    Y = serverY,
                    Width = ServerWidth,
                    Height = boxHeight
                });
                geometry[server.Id] = (ServerX, serverY, ServerWidth, boxHeight);

                for (var i = 0; i < children.Count; i++)
                {
                    var chipY = serverY + ServerHeader + i * (ChipHeight + ChipGap);
                    var (child, indent) = children[i];
                    var chipX = ServerX + 12 + indent * 18;
                    var chipWidth = ServerWidth - 24 - indent * 18;
                    _parentOf[child.Id] = server.Id;
                    var chip = new GraphChipVm
                    {
                        Id = child.Id,
                        Label = indent > 0 ? "└ " + labelById[child.Id] : labelById[child.Id],
                        Type = child.Type,
                        Tooltip = tooltipById[child.Id],
                        X = chipX,
                        Y = chipY,
                        Width = chipWidth,
                        Height = ChipHeight
                    };
                    ApplyNodeHealth(chip, child);
                    Chips.Add(chip);
                    geometry[child.Id] = (chipX, chipY, chipWidth, ChipHeight);
                }

                serverY += boxHeight + 18;
            }

            // 中列：未托管的应用
            double appY = 20;
            foreach (var app in standaloneApps)
            {
                var node = new GraphNodeVm
                {
                    Id = app.Id,
                    Label = labelById[app.Id],
                    Type = "application",
                    Tooltip = tooltipById[app.Id],
                    X = StandaloneAppX,
                    Y = appY,
                    Width = StandaloneAppWidth,
                    Height = StandaloneAppHeight
                };
                ApplyNodeHealth(node, app);
                Nodes.Add(node);
                geometry[app.Id] = (StandaloneAppX, appY, StandaloneAppWidth, StandaloneAppHeight);
                appY += StandaloneAppHeight + 12;
            }

            // 右列：未托管的数据库
            double dsY = 20;
            foreach (var ds in standaloneDs)
            {
                var node = new GraphNodeVm
                {
                    Id = ds.Id,
                    Label = labelById[ds.Id],
                    Type = ds.Type,
                    Tooltip = tooltipById[ds.Id],
                    X = DsX,
                    Y = dsY,
                    Width = DsWidth,
                    Height = DsHeight
                };
                ApplyNodeHealth(node, ds);
                Nodes.Add(node);
                geometry[ds.Id] = (DsX, dsY, DsWidth, DsHeight);
                dsY += DsHeight + 12;
            }

            _geometry.Clear();
            foreach (var kv in geometry)
                _geometry[kv.Key] = kv.Value;

            // 热路径索引：按 id 查找、父→子反查（替代每帧 LINQ 扫描）
            _itemsById.Clear();
            foreach (var b in Boxes) _itemsById[b.Id] = b;
            foreach (var c in Chips) _itemsById[c.Id] = c;
            foreach (var n in Nodes) _itemsById[n.Id] = n;
            _allItems = Boxes.Concat<GraphItemVm>(Chips).Concat(Nodes).ToArray();
            OnPropertyChanged(nameof(IsEmptyGraph));

            _childrenOf.Clear();
            foreach (var kv in _parentOf)
            {
                if (!_childrenOf.TryGetValue(kv.Value, out var list))
                    _childrenOf[kv.Value] = list = new List<string>();
                list.Add(kv.Key);
            }

            ApplyManualLayout();
            // 托管子节点必须完全位于所属服务器内（修正历史/异常布局）
            foreach (var childId in _parentOf.Keys.ToArray())
                ClampChild(childId);
            // 新增(无保存位置)的节点在手动布局下会沿用默认堆叠坐标，可能压到已拖动的节点上 → 自动避让
            RelocateUnsavedItems();
            // 解除 runsOn 后残留的独立节点若仍落在服务器内，移到就近空白处
            RelocateOrphanedStandalone();
            RebuildEdges(false);

            CanvasHeight = Math.Max(Math.Max(Math.Max(serverY, appY), dsY) + 40, 1400);
            CanvasWidth = Math.Max(Math.Max(DsX + DsWidth, MaxRight()) + 40, 2000);
            StatusMessage = $"服务器 {servers.Length} 台, 应用 {apps.Length} 个, 数据源 {datasources.Length} 个, 连线 {Edges.Count} 条 (runsOn 以嵌套展示)";
            GraphLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载拓扑失败: {ex.Message}";
        }
    }

    /// <summary>DEBUG 压测：构造大规模合成拓扑走同一渲染管线（不落盘）。</summary>
    public void LoadSynthetic(int serverCount = 8, int appCount = 120, int dsCount = 80)
    {
        var graph = BuildSyntheticGraph(serverCount, appCount, dsCount);
        _synthetic = true;
        ApplyGraph(graph);
        StatusMessage = $"压测图: {StatusMessage}（不落盘，点「刷新」恢复真实图）";
    }

    private static TopologyGraph BuildSyntheticGraph(int servers, int apps, int ds)
    {
        var nodes = new List<TopologyNode>();
        var edges = new List<TopologyEdge>();
        if (servers < 1) servers = 1;
        if (ds < 1) ds = 1;

        for (var i = 0; i < servers; i++)
            nodes.Add(new TopologyNode { Id = $"stress:ssh:{i}", Label = $"压测服务器-{i}", Type = "ssh" });

        for (var i = 0; i < apps; i++)
        {
            nodes.Add(new TopologyNode { Id = $"stress:app:{i}", Label = $"压测应用-{i}", Type = "application" });
            if (i % 2 == 0)
                edges.Add(new TopologyEdge { From = $"stress:app:{i}", To = $"stress:ssh:{i % servers}", Type = "runsOn" });
        }

        for (var i = 0; i < ds; i++)
        {
            nodes.Add(new TopologyNode { Id = $"stress:ds:{i}", Label = $"压测库-{i}", Type = "datasource" });
            if (i % 2 == 0)
                edges.Add(new TopologyEdge { From = $"stress:ds:{i}", To = $"stress:ssh:{i % servers}", Type = "runsOn" });
        }

        for (var i = 0; i < apps; i++)
            edges.Add(new TopologyEdge { From = $"stress:app:{i}", To = $"stress:ds:{(i * 7) % ds}", Type = "connectsTo", Source = "discovered" });

        for (var j = 0; j < servers; j++)
            for (var k = 0; k < 10; k++)
                edges.Add(new TopologyEdge { From = $"stress:ssh:{j}", To = $"stress:ds:{(j * 10 + k) % ds}", Type = "canAccess", Source = "discovered" });

        for (var i = 0; i < apps; i += 3)
            edges.Add(new TopologyEdge { From = $"stress:app:{i}", To = $"stress:app:{(i + 1) % apps}", Type = "relatedTo" });

        return new TopologyGraph { Nodes = nodes.ToArray(), Edges = edges.ToArray() };
    }

    /// <summary>记录一帧（mousemove 内的移动/缩放处理）耗时，仅供 PerfLog 会话汇总。</summary>
    public void TrackFrame(double ms)
    {
        if (!PerfLog.Enabled)
            return;
        _frameCount++;
        _frameTotalMs += ms;
        if (ms > _frameMaxMs)
            _frameMaxMs = ms;
    }

    /// <summary>拖动/缩放会话结束时写一行汇总（避免逐帧刷屏）。</summary>
    public void FlushDragPerf()
    {
        if (_frameCount == 0)
            return;
        PerfLog.Write($"DragSession: frames={_frameCount} total={_frameTotalMs:F1}ms avg={_frameTotalMs / _frameCount:F2}ms max={_frameMaxMs:F2}ms");
        _frameCount = 0;
        _frameTotalMs = 0;
        _frameMaxMs = 0;
    }

    public RelationTypeOption[] RelationTypes { get; } =
    {
        new("runsOn", "运行于服务器"),
        new("runsIn", "运行于容器"),
        new("connectsTo", "连接到"),
        new("canAccess", "可访问"),
        new("upstreamOf", "上游依赖"),
        new("accessedVia", "通过跳板访问"),
        new("relatedTo", "相关")
    };
    public string SelectedEdgeSummary => _selectedEdge == null
        ? string.Empty
        : $"{_selectedEdge.From}  →  {_selectedEdge.To}" +
          (_selectedEdge.IsDiscovered ? "（自动发现，仅可删除）" :
              _selectedEdge.IsDerived ? "（配置推导，请在资产配置中修改）" : "（手动）");
    public bool SelectedEdgeEditable => _selectedEdge is { IsDiscovered: false, IsDerived: false };
    public bool SelectedEdgeDeletable => _selectedEdge is { IsDerived: false };
    public string SelectedEdgeType { get => _selEdgeType; set { _selEdgeType = value; OnPropertyChanged(); } }
    public string SelectedEdgeNote { get => _selEdgeNote; set { _selEdgeNote = value; OnPropertyChanged(); } }
    public string SelectedEdgeHint { get => _selEdgeHint; set { _selEdgeHint = value; OnPropertyChanged(); } }
    public bool HasSelection => _selectedId != null;
    public string? SelectedIdOrNull => _selectedId;
    public bool IsLinking => _linkFrom != null;
    public bool HasSelectedEdge => _selectedEdge != null;
    public GraphEdgeVm? SelectedEdgeOrNull => _selectedEdge;

    /// <summary>确认对话框回调（由视图注入）。返回 true 表示用户确认。</summary>
    public Func<string, bool>? Confirm { get; set; }
    public Geometry LinkGeometry { get => _linkGeometry; private set { _linkGeometry = value; OnPropertyChanged(); } }
    public Geometry SelectedEdgeLine { get => _selectedEdgeLine; private set { _selectedEdgeLine = value; OnPropertyChanged(); } }
    public double SelX { get => _selX; set { if (_selX == value) return; _selX = value; OnPropertyChanged(); } }
    public double SelY { get => _selY; set { if (_selY == value) return; _selY = value; OnPropertyChanged(); } }
    public double SelW { get => _selW; set { if (_selW == value) return; _selW = value; OnPropertyChanged(); } }
    public double SelH { get => _selH; set { if (_selH == value) return; _selH = value; OnPropertyChanged(); } }

    private GraphItemVm? FindItem(string id) =>
        _itemsById.TryGetValue(id, out var item) ? item : null;

    private double MaxRight() =>        Boxes.Concat<GraphItemVm>(Chips).Concat(Nodes).Select(i => i.X + i.Width).DefaultIfEmpty(0).Max();

    private double MaxBottom() =>
        Boxes.Concat<GraphItemVm>(Chips).Concat(Nodes).Select(i => i.Y + i.Height).DefaultIfEmpty(0).Max();

    /// <summary>所有节点/区块的包围盒（用于"适应画布"）。</summary>
    public Rect ContentBounds()
    {
        var items = Boxes.Concat<GraphItemVm>(Chips).Concat(Nodes).ToArray();
        if (items.Length == 0)
            return new Rect(0, 0, CanvasWidth, CanvasHeight);

        var minX = items.Min(i => i.X);
        var minY = items.Min(i => i.Y);
        var maxX = items.Max(i => i.X + i.Width);
        var maxY = items.Max(i => i.Y + i.Height);
        return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    public bool IsServerNode(string id) =>
        _itemsById.TryGetValue(id, out var item) && item is GraphBoxVm;


    /// <summary>应用手动布局覆盖（节点位置/尺寸）。</summary>
    private void ApplyManualLayout()
    {
        var layout = _layoutHistory.Get();
        if (!layout.IsManual)
            return;

        foreach (var item in Boxes.Concat<GraphItemVm>(Chips).Concat(Nodes))
        {
            NodeLayout? nl = layout.Nodes.TryGetValue(item.Id, out var direct) ? direct : null;
            if (nl is null)
            {
                var node = _graph?.Nodes.FirstOrDefault(n => n.Id == item.Id);
                if (node is not null)
                {
                    nl = ProfileLayoutIds(node)
                        .Select(id => layout.Nodes.TryGetValue(id, out var candidate) ? candidate : null)
                        .FirstOrDefault(candidate => candidate is not null);
                }
            }

            if (nl is null)
                continue;

            item.X = nl.X;
            item.Y = nl.Y;
            item.Width = nl.Width;
            item.Height = nl.Height;
            _geometry[item.Id] = (nl.X, nl.Y, nl.Width, nl.Height);
        }
    }

    private EdgeLayout? FindEdgeLayout(TopologyLayout layout, TopologyEdge edge)
    {
        if (layout.Edges.TryGetValue($"{edge.From}|{edge.Type}|{edge.To}", out var direct))
            return direct;

        foreach (var from in LayoutIds(edge.From))
        {
            foreach (var to in LayoutIds(edge.To))
            {
                if (layout.Edges.TryGetValue($"{from}|{edge.Type}|{to}", out var alias))
                    return alias;
            }
        }

        return null;
    }

    private IEnumerable<string> LayoutIds(string nodeId)
    {
        yield return nodeId;

        var node = _graph?.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null)
            yield break;

        foreach (var id in ProfileLayoutIds(node))
            yield return id;
    }

    /// <summary>重建所有连线（fast=true 时用简化路由，用于拖动过程）。</summary>
    private void RebuildEdges(bool fast)
    {
        using var perf = PerfLog.Scope(fast ? "RebuildEdges(fast)" : "RebuildEdges(full)", () => $"edges={Edges.Count}");

        Edges.Clear();
        Hops.Clear();
        Labels.Clear();
        if (_graph == null)
            return;

        var hostedBy = _graph.Edges
            .Where(e => e.Type == "runsOn")
            .GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.First().To);

        var layout = _layoutHistory.Get();
        var evidenceByKey = _graph.Edges
            .Where(e => !string.IsNullOrEmpty(e.Evidence))
            .GroupBy(e => $"{e.From}|{e.Type}|{e.To}")
            .ToDictionary(g => g.Key, g => g.First().Evidence!);
        var disputedKeys = _graph.Edges
            .Where(e => e.Disputed)
            .Select(e => $"{e.From}|{e.Type}|{e.To}")
            .ToHashSet(StringComparer.Ordinal);
        var derivedKeys = _graph.Edges
            .Where(e => string.Equals(e.Source, "derived", StringComparison.OrdinalIgnoreCase))
            .Select(e => $"{e.From}|{e.Type}|{e.To}")
            .ToHashSet(StringComparer.Ordinal);
        var disputeReasons = _graph.Edges
            .Where(e => !string.IsNullOrWhiteSpace(e.DisputeReason))
            .GroupBy(e => $"{e.From}|{e.Type}|{e.To}")
            .ToDictionary(g => g.Key, g => g.First().DisputeReason, StringComparer.Ordinal);
        var drawable = _graph.Edges
            .Where(e => e.Type != "runsOn")
            .Where(e => !(e.Type == "canAccess" && hostedBy.TryGetValue(e.To, out var host) && host == e.From))
            .Where(e => _geometry.ContainsKey(e.From) && _geometry.ContainsKey(e.To))
            .Select(e => TopologyRouteEngine.ComputeRoute(
                e.From, e.To, e.Type,
                string.Equals(e.Source, "discovered", StringComparison.OrdinalIgnoreCase),
                _geometry[e.From], _geometry[e.To],
                FindEdgeLayout(layout, e)))
            .ToList();

        foreach (var group in drawable.GroupBy(r => r.CorridorKey))
        {
            var lanes = group.OrderBy(r => r.OrderKey).ToArray();
            for (var i = 0; i < lanes.Length; i++)
                lanes[i].Lateral = (i - (lanes.Length - 1) / 2.0) * LaneGap;
        }

        var obstacles = Boxes.Select(b => Rect.Inflate(new Rect(b.X, b.Y, b.Width, b.Height), 5, 5))
            .Concat(Chips.Select(c => Rect.Inflate(new Rect(c.X, c.Y, c.Width, c.Height), 5, 5)))
            .Concat(Nodes.Select(n => Rect.Inflate(new Rect(n.X, n.Y, n.Width, n.Height), 5, 5)))
            .ToList();

        // 共享上下文：障碍索引与坐标压缩只建一次（而非每边）
        var ctx = new TopologyRouteEngine.RouteContext(obstacles);

        var segments = new List<TopologyRouteEngine.EdgeSegment>();
        foreach (var route in drawable)
        {
            try
            {
                var points = TopologyRouteEngine.BuildRoute(route, ctx, fast);

                // 兜底：至少两个点，避免索引越界
                if (points.Count < 2)
                    points = new List<Point> { route.S, new Point(route.S.X + 1, route.S.Y) };

                var key = $"{route.From}|{route.Type}|{route.To}";
                var disputed = disputedKeys.Contains(key);
                var stroke = disputed ? Brushes.IndianRed : TopologyRouteEngine.StrokeFor(route.Type, route.IsDiscovered);
                var style = TopologyRouteEngine.StyleFor(route.Type, route.IsDiscovered);
                var (labelX, labelY) = TopologyRouteEngine.LabelPoint(points, route.Horizontal);
                var line = TopologyRouteEngine.BuildRoundedPolyline(points, 8);
                var dot = TopologyRouteEngine.BuildDot(points[0], 4);
                var arrow = TopologyRouteEngine.BuildArrow(points[^1], points[^2], route.IsDiscovered);

                var vm = new GraphEdgeVm
                {
                    Key = key,
                    Line = line,
                    Dot = dot,
                    Arrow = arrow,
                    Stroke = stroke,
                    DashArray = style.DashArray,
                    Thickness = style.Thickness,
                    Type = route.Type,
                    Label = disputed ? $"⚠ {route.Type}" :
                        route.Type is "canAccess" or "connectsTo" ? string.Empty : route.Type,
                    From = route.From,
                    To = route.To,
                    Evidence = evidenceByKey.GetValueOrDefault(key) ?? string.Empty,
                    DisputeReason = disputeReasons.GetValueOrDefault(key),
                    Points = points,
                    LabelX = labelX,
                    LabelY = labelY,
                    IsDiscovered = route.IsDiscovered,
                    IsDerived = derivedKeys.Contains(key),
                    IsDisputed = disputed
                };
                Edges.Add(vm);
                if (vm.Label.Length > 0)
                    Labels.Add(vm);

                if (!fast)
                {
                    var order = Edges.Count - 1;
                    for (var k = 0; k + 1 < points.Count; k++)
                        segments.Add(new TopologyRouteEngine.EdgeSegment(points[k], points[k + 1], stroke, order));
                }
            }
            catch
            {
                // 单条边计算失败不影响其它边
            }
        }

        // 拖动过程中(快速)不计算交叉过桥，降低开销
        if (!fast)
        {
            foreach (var hop in TopologyRouteEngine.DetectHops(segments))
            {
                Hops.Add(new GraphHopVm
                {
                    Disc = TopologyRouteEngine.BuildHopDisc(hop.Center),
                    Bridge = TopologyRouteEngine.BuildShortLine(hop.Center, hop.BottomDir),
                    Arc = TopologyRouteEngine.BuildHopArc(hop.Center, hop.TopDir),
                    BridgeStroke = hop.BottomStroke,
                    ArcStroke = hop.TopStroke
                });
            }
        }

        if (_selectedId != null)
            ApplyImpactHighlight(_selectedId);
    }

    // ---- 交互 ----

    /// <summary>命中最上层叶子节点（应用/库，含托管子节点）。</summary>
    public string? HitLeaf(Point point)
    {
        foreach (var n in Nodes) if (Contains(n, point, 2)) return n.Id;
        foreach (var c in Chips) if (Contains(c, point, 2)) return c.Id;
        return null;
    }

    /// <summary>命中服务器区块。</summary>
    public string? HitBox(Point point)
    {
        foreach (var b in Boxes) if (Contains(b, point)) return b.Id;
        return null;
    }

    public string? HitTest(Point point) => HitLeaf(point) ?? HitBox(point);

    private static bool Contains(GraphItemVm item, Point p, double tolerance = 0) =>
        p.X >= item.X - tolerance && p.X <= item.X + item.Width + tolerance &&
        p.Y >= item.Y - tolerance && p.Y <= item.Y + item.Height + tolerance;

    /// <summary>
    /// 反向 BFS（谁依赖当前节点）最多 2 跳；根节点与路径上的节点/边保持高亮，其余降透明。
    /// </summary>
    private void ApplyImpactHighlight(string? rootId)
    {
        if (_graph == null || string.IsNullOrWhiteSpace(rootId))
        {
            foreach (var item in _allItems)
                item.IsDimmed = false;
            foreach (var edge in Edges)
                edge.IsDimmed = false;
            ImpactSummary = string.Empty;
            return;
        }

        var visible = _geometry.Keys.ToHashSet(StringComparer.Ordinal);
        var (impacted, impactedEdges) = TopologyGeometry.ComputeImpact(rootId, _graph.Edges, visible);

        foreach (var item in _allItems)
            item.IsDimmed = !impacted.Contains(item.Id);
        foreach (var edge in Edges)
            edge.IsDimmed = !impactedEdges.Contains($"{edge.From}|{edge.Type}|{edge.To}");

        var count = Math.Max(0, impacted.Count - 1);
        ImpactSummary = count == 0
            ? "影响面: 没有已知的反向依赖（2 跳内）"
            : $"影响面: {count} 个资产依赖它（反向 2 跳）";
    }

    /// <summary>反向影响面：沿 incoming 边最多 maxHops 跳，返回节点与路径边键。</summary>
    public void ClearSelection() => SelectNode(null);

    /// <summary>按当前几何刷新选中框/手柄/端口（落点回退后调用）。</summary>
    public void RefreshSelection() => UpdateSelectionRect();

    private void UpdateSelectionRect()
    {
        if (_selectedId != null && FindItem(_selectedId) is { } item)
        {
            SelX = item.X;
            SelY = item.Y;
            SelW = item.Width;
            SelH = item.Height;
            UpdateHandles();
            UpdatePorts();
        }
        else
        {
            SelX = SelY = SelW = SelH = 0;
            Handles.Clear();
            Ports.Clear();
        }
    }

    private static readonly string[] HandleTags = { "nw", "ne", "se", "sw" };
    private static readonly string[] PortTags = { "left", "right", "top", "bottom" };

    private static void EnsureCount(ObservableCollection<GraphHandleVm> items, string[] tags)
    {
        if (items.Count == tags.Length)
            return;
        items.Clear();
        foreach (var tag in tags)
            items.Add(new GraphHandleVm { Tag = tag });
    }


    /// <summary>端口圆心：在节点对应边的外侧（圆半径 TopologyGeometry.PortRadius + PortGap 间隙），与边缩放带互不重叠。</summary>
    private void UpdatePorts()
    {
        EnsureCount(Ports, PortTags);
        var sel = new Rect(SelX, SelY, SelW, SelH);
        for (var i = 0; i < PortTags.Length; i++)
        {
            var c = TopologyGeometry.PortCenterFor(sel, PortTags[i]);
            var bounds = new Rect(c.X - TopologyGeometry.PortRadius, c.Y - TopologyGeometry.PortRadius, TopologyGeometry.PortRadius * 2, TopologyGeometry.PortRadius * 2);
            Ports[i].Bounds = bounds;
            Ports[i].Shape = Freeze(new EllipseGeometry(c, TopologyGeometry.PortRadius, TopologyGeometry.PortRadius));
        }
    }

    private void UpdateHandles()
    {
        EnsureCount(Handles, HandleTags);
        const double size = 8;
        var lx = SelX;
        var ty = SelY;
        var rx = SelX + SelW;
        var by = SelY + SelH;
        for (var i = 0; i < HandleTags.Length; i++)
        {
            var c = HandleTags[i] switch
            {
                "nw" => new Point(lx, ty),
                "ne" => new Point(rx, ty),
                "se" => new Point(rx, by),
                _ => new Point(lx, by)
            };
            var bounds = new Rect(c.X - size / 2, c.Y - size / 2, size, size);
            Handles[i].Bounds = bounds;
            Handles[i].Shape = Freeze(new RectangleGeometry(bounds));
        }
    }

    public string? HitHandle(Point point)
    {
        foreach (var handle in Handles)
        {
            var bounds = handle.Bounds;
            bounds.Inflate(2, 2);
            if (bounds.Contains(point))
                return handle.Tag;
        }

        return null;
    }

    /// <summary>四边内侧缩放带厚度（节点边内侧），与角手柄(±6px)以 cornerGap 分隔。</summary>

    /// <summary>
    /// 选中节点四边内侧的缩放带命中：返回 "n"/"s"/"w"/"e"，否则 null。
    /// 纯函数：带在节点边内侧 6px、两端各避开角手柄 8px（角手柄优先，用于对角缩放）。
    /// </summary>
    public string? HitEdgeHandle(Point point) =>
        _selectedId == null ? null : TopologyGeometry.EdgeHandleAt(new Rect(SelX, SelY, SelW, SelH), point);

    /// <summary>
    /// 鼠标拖动：位置 = 快照起点 + 相对锚点的总位移（再做 10px 吸附）。
    /// 每次从「起点 + 总位移」直接推导，与事件顺序无关 —— 来回移动鼠标不产生累积漂移（旧增量模型会逐步丢舍入量）。
    /// </summary>
    public void MoveNode(string id, Point point)
    {
        if (_dragAnchor is not { } anchor || _dragSnapshot == null ||
            !_dragSnapshot.TryGetValue(id, out var baseRect))
            return;

        var totalX = point.X - anchor.X;
        var totalY = point.Y - anchor.Y;

        var moved = MoveToSnapped(id, baseRect.X + totalX, baseRect.Y + totalY);
        if (_childrenOf.TryGetValue(id, out var children))
        {
            foreach (var child in children)
            {
                if (_dragSnapshot.TryGetValue(child, out var childBase))
                    moved |= MoveToSnapped(child, childBase.X + totalX, childBase.Y + totalY);
            }
        }

        if (!moved)
            return;
        RequestFastRebuild(id);
        UpdateSelectionRect();
    }

    /// <summary>按绝对坐标移动（10px 吸附）；未跨吸附边界时返回 false（跳过连线重算）。</summary>
    private bool MoveToSnapped(string id, double rawX, double rawY)
    {
        if (!_geometry.TryGetValue(id, out var g))
            return false;

        var x = Math.Round(rawX / 10.0) * 10.0;
        var y = Math.Round(rawY / 10.0) * 10.0;
        if (Math.Abs(x - g.X) < 0.01 && Math.Abs(y - g.Y) < 0.01)
            return false;

        ApplyGeometry(id, x, y, g.W, g.H);
        return true;
    }

    /// <summary>方向键增量移动（正好 ±10px，吸附无损；含子节点）。</summary>
    private void MoveByDelta(string id, double dx, double dy)
    {
        if (!_geometry.TryGetValue(id, out _))
            return;

        MoveSingle(id, dx, dy);
        if (_childrenOf.TryGetValue(id, out var children))
        {
            foreach (var child in children)
                MoveSingle(child, dx, dy);
        }
    }

    /// <summary>拖动过程中的连线重算做节流（约 25ms 一次）+ 尾部补算，且只原地更新受影响连线。</summary>
    private void RequestFastRebuild(string id)
    {
        _fastNodeId = id;

        var now = _clock.ElapsedMilliseconds;
        if (now - _lastFastMs >= FastIntervalMs)
        {
            _lastFastMs = now;
            RebuildIncidentEdges(id, fast: true);
            return;
        }

        if (_fastPending)
            return;

        _fastPending = true;
        _fastTimer ??= CreateFastTimer();
        _fastTimer.Stop();
        _fastTimer.Start();
    }

    private DispatcherTimer CreateFastTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FastIntervalMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _fastPending = false;
            _lastFastMs = _clock.ElapsedMilliseconds;
            try
            {
                if (_fastNodeId is { } nodeId)
                    RebuildIncidentEdges(nodeId, fast: true);
            }
            catch
            {
                // 拖动过程忽略
            }
        };
        return timer;
    }

    /// <summary>只重算并原地更新与某节点相连的连线（拖动过程，不重建集合、不触发容器抖动）。</summary>
    private void RebuildIncidentEdges(string id, bool fast)
    {
        if (_graph == null || string.IsNullOrEmpty(id))
            return;

        _childrenOf.TryGetValue(id, out var children);
        var layout = _layoutHistory.Get();
        foreach (var e in Edges)
        {
            if (e.From != id && e.To != id &&
                (children == null || (!children.Contains(e.From) && !children.Contains(e.To))))
                continue;
            if (!_geometry.TryGetValue(e.From, out var from) || !_geometry.TryGetValue(e.To, out var to))
                continue;

            var route = TopologyRouteEngine.ComputeRoute(
                e.From, e.To, e.Type, e.IsDiscovered,
                from, to,
                layout.Edges.TryGetValue(e.Key, out var el) ? el : null);

            var points = fast
                ? TopologyRouteEngine.FallbackRoute(route)
                : TopologyRouteEngine.BuildRoute(route, Array.Empty<Rect>(), false);
            if (points.Count < 2)
                points = new List<Point> { route.S, new Point(route.S.X + 1, route.S.Y) };

            var stroke = TopologyRouteEngine.StrokeFor(route.Type, route.IsDiscovered);
            var style = TopologyRouteEngine.StyleFor(route.Type, route.IsDiscovered);
            var (lx, ly) = TopologyRouteEngine.LabelPoint(points, route.Horizontal);

            // 路径未变则跳过几何重建（快/慢路由一致时零开销）
            if (!SamePoints(points, e.Points))
            {
                e.Line = TopologyRouteEngine.BuildRoundedPolyline(points, 8);
                e.Dot = TopologyRouteEngine.BuildDot(points[0], 4);
                e.Arrow = TopologyRouteEngine.BuildArrow(points[^1], points[^2], route.IsDiscovered);
                e.Points = points;
            }

            e.Stroke = stroke;
            e.DashArray = style.DashArray;
            e.Thickness = style.Thickness;
            e.LabelX = lx;
            e.LabelY = ly;
        }
    }

    private static bool SamePoints(IReadOnlyList<Point> a, IReadOnlyList<Point> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i].X - b[i].X) > 0.01 || Math.Abs(a[i].Y - b[i].Y) > 0.01)
                return false;
        }
        return true;
    }

    private void MoveSingle(string id, double dx, double dy)
    {
        if (!_geometry.TryGetValue(id, out var g))
            return;

        // 网格吸附(10px)
        var x = Math.Round((g.X + dx) / 10.0) * 10.0;
        var y = Math.Round((g.Y + dy) / 10.0) * 10.0;
        ApplyGeometry(id, x, y, g.W, g.H);
    }


    /// <summary>
    /// 按相对锚点的总位移计算缩放候选（对边固定、最小尺寸钳制、10px 吸附）。
    /// 纯函数：结果只取决于 (start, total) —— 与事件路径无关，来回拖动不漂移。
    /// </summary>
    public void ResizeSelected(string handle, Point point)
    {
        if (_selectedId == null || _dragAnchor is not { } anchor ||
            _dragStartRect is not { } start || !_geometry.TryGetValue(_selectedId, out var g))
            return;

        var (x, y, w, h) = TopologyGeometry.ComputeResize(handle, start, point.X - anchor.X, point.Y - anchor.Y);

        // 网格吸附后未变化时先早退（高频 mousemove 下跳过后续校验）
        if (Math.Abs(x - g.X) < 0.01 && Math.Abs(y - g.Y) < 0.01 &&
            Math.Abs(w - g.W) < 0.01 && Math.Abs(h - g.H) < 0.01)
            return;

        var candidate = new Rect(x, y, w, h);

        // 与其它节点区域非法重叠则跳过本次调整（碰到边界即停）；
        // 服务器自身的托管子节点不参与校验（下面会被收紧到新矩形）
        if (ServerOverlapInvalid(_selectedId, candidate))
        {
            // 吸收被挡位移：以当前几何为新基准重锚，反向拖动无死区、也不累积漂移
            _dragStartRect = new Rect(g.X, g.Y, g.W, g.H);
            _dragAnchor = point;
            return;
        }

        ApplyGeometry(_selectedId, x, y, w, h);

        // 服务器缩放时子节点自动收紧到新矩形（子节点不再挡住缩放）
        if (_childrenOf.TryGetValue(_selectedId, out var children))
        {
            foreach (var child in children)
            {
                if (!_geometry.TryGetValue(child, out var cg))
                    continue;
                var clamped = TopologyGeometry.ClampChildTo(candidate, new Rect(cg.X, cg.Y, cg.W, cg.H), ServerHeader);
                ApplyGeometry(child, clamped.X, clamped.Y, clamped.Width, clamped.Height);
            }
        }

        RequestFastRebuild(_selectedId);
        UpdateSelectionRect();
    }

    private void ApplyGeometry(string id, double x, double y, double w, double h)
    {
        _geometry[id] = (x, y, w, h);
        if (FindItem(id) is { } item)
        {
            item.X = x;
            item.Y = y;
            item.Width = w;
            item.Height = h;
        }
    }

    /// <summary>
    /// 把子节点矩形夹进父容器矩形（左右/底留 6px，顶部留出标题栏 header）。
    /// 容器过小时 <see cref="Clamp"/> 按 (min+max)/2 居中放置（不会产生反向钳制）。纯函数，便于单测。
    /// </summary>
    private void ClampChild(string childId)
    {
        if (!_parentOf.TryGetValue(childId, out var parentId) ||
            !_geometry.TryGetValue(parentId, out var parent) ||
            !_geometry.TryGetValue(childId, out var child))
            return;

        var clamped = TopologyGeometry.ClampChildTo(
            new Rect(parent.X, parent.Y, parent.W, parent.H),
            new Rect(child.X, child.Y, child.W, child.H),
            ServerHeader);
        ApplyGeometry(childId, clamped.X, clamped.Y, clamped.Width, clamped.Height);
    }

    /// <summary>把当前几何保存为手动布局。</summary>
    public void CommitLayout()
    {
        using var perf = PerfLog.Scope("CommitLayout", () => $"edges={Edges.Count}");

        // 取消可能残留的"尾部补算"定时器，避免松手后用快速路由覆盖最终完整路由
        if (_fastTimer is { IsEnabled: true })
            _fastTimer.Stop();
        _fastPending = false;

        // 撤销快照: 保存修改前的布局
        _layoutHistory.PushUndo();

        // 压测图不写 topology-layout.json（避免合成 id 污染真实布局）
        if (!_synthetic)
        {
            var layout = new TopologyLayout { IsManual = true };
            foreach (var kv in _geometry)
                layout.Nodes[kv.Key] = new NodeLayout { X = kv.Value.X, Y = kv.Value.Y, Width = kv.Value.W, Height = kv.Value.H };

            _layoutHistory.Save(layout);
        }
        CanvasWidth = Math.Max(MaxRight() + 40, 2000);
        CanvasHeight = Math.Max(MaxBottom() + 40, 1400);
        RebuildEdges(false);
        StatusMessage = _synthetic ? "压测图已布局 (不落盘)" : "已保存手动布局 (topology-layout.json)";
    }

    // ---- 拖动落点：容器(runsOn)自动判定 ----

    /// <summary>开始拖动/缩放：记录几何快照与鼠标锚点（绝对定位基准）。</summary>
    public void BeginNodeDrag(string id, Point? anchor = null)
    {
        _dragSnapshot = new Dictionary<string, (double X, double Y, double W, double H)>(_geometry);
        _dragStartRect = _geometry.TryGetValue(id, out var g) ? new Rect(g.X, g.Y, g.W, g.H) : null;
        _dragAnchor = anchor;
    }

    /// <summary>未发生实际位移时取消拖动（不落盘、不产生撤销点）。</summary>
    public void CancelDrag() => ClearDragState();

    /// <summary>清除拖动状态（快照、缩放基准、鼠标锚点）。</summary>
    private void ClearDragState()
    {
        _dragSnapshot = null;
        _dragStartRect = null;
        _dragAnchor = null;
    }

    /// <summary>结束拖动：判定是否进出服务器容器，必要时增删 runsOn 关系。</summary>
    public async Task EndNodeDragAsync(string id)
    {
        try
        {
            if (_dragSnapshot == null || !_geometry.TryGetValue(id, out var g))
            {
                CommitLayout();
                return;
            }

            var rect = new Rect(g.X, g.Y, g.W, g.H);
            var servers = Boxes.Select(b => (b.Id, Rect: new Rect(b.X, b.Y, b.Width, b.Height))).ToArray();

            // 服务器本身：禁止与其它服务器重叠
            if (IsServerNode(id))
            {
                if (servers.Any(s => s.Id != id && rect.IntersectsWith(s.Rect)))
                {
                    RejectDrag("服务器之间不能重叠");
                    return;
                }

                CommitLayout();
                return;
            }

            _parentOf.TryGetValue(id, out var currentParent);

            // 已托管子节点：允许在所属容器内自由移动（部分重叠亦可），仅在完全离开或进入其它服务器时改关系
            if (currentParent != null)
            {
                var other = servers.FirstOrDefault(s => s.Id != currentParent && s.Rect.Contains(rect));
                if (other.Id != null)
                {
                    var move = Confirm?.Invoke(
                        $"将 \"{id}\" 从服务器 {currentParent} 移到 {other.Id} 会删除原 runsOn 并新建，是否继续？") ?? false;
                    if (!move)
                    {
                        RejectDrag("已取消移动");
                        return;
                    }

                    if (!await TryAddRunsOnAsync(id, currentParent, other.Id))
                        return;

                    GrowServerToFit(other.Id, rect);
                    CommitLayout();
                    Load();
                    StatusMessage = $"已建立 runsOn: {id} → {other.Id}";
                    return;
                }

                if (_geometry.TryGetValue(currentParent, out var parentBox) &&
                    new Rect(parentBox.X, parentBox.Y, parentBox.W, parentBox.H).IntersectsWith(rect))
                {
                    ClampChild(id);
                    if (_geometry.TryGetValue(id, out var cg) &&
                        servers.Any(s => s.Id != currentParent && s.Rect.IntersectsWith(new Rect(cg.X, cg.Y, cg.W, cg.H))))
                    {
                        RejectDrag("不能与其它服务器重叠");
                        return;
                    }

                    CommitLayout();
                    return;
                }

                var startedInside = _dragStartRect is { } sr
                    && _geometry.TryGetValue(currentParent, out var pb)
                    && new Rect(pb.X, pb.Y, pb.W, pb.H).Contains(sr);
                if (!startedInside)
                {
                    CommitLayout();
                    return;
                }

                var remove = Confirm?.Invoke($"\"{id}\" 已移出服务器 {currentParent}，是否删除其 runsOn 关系？") ?? false;
                if (!remove)
                {
                    RejectDrag("已取消移出（保持托管）");
                    return;
                }

                if (!await TryRemoveRunsOnAsync(id, currentParent))
                    return;

                CommitLayout();
                Load();
                StatusMessage = $"已解除 runsOn: {id}";
                return;
            }

            // 独立节点：完全落入服务器 → 建立 runsOn
            var inside = servers.FirstOrDefault(s => s.Rect.Contains(rect));
            if (inside.Id != null)
            {
                if (!await TryAddRunsOnAsync(id, null, inside.Id))
                    return;

                GrowServerToFit(inside.Id, rect);
                CommitLayout();
                Load();
                StatusMessage = $"已建立 runsOn: {id} → {inside.Id}";
                return;
            }

            // 独立节点部分重叠服务器 → 禁止
            if (servers.Any(s => rect.IntersectsWith(s.Rect)))
            {
                RejectDrag("节点不能与服务器部分重叠：需完全移入（建立 runsOn）或完全移出");
                return;
            }

            CommitLayout();
        }
        finally
        {
            ClearDragState();
        }
    }

    private void GrowServerToFit(string serverId, Rect child)
    {
        if (!_geometry.TryGetValue(serverId, out var sg))
            return;

        var needW = child.Right - sg.X + 12;
        var needH = child.Bottom - sg.Y + 12;
        if (needW > sg.W || needH > sg.H)
            ApplyGeometry(serverId, sg.X, sg.Y, Math.Max(sg.W, needW), Math.Max(sg.H, needH));
    }

    /// <summary>该矩形是否与服务器/节点区域非法重叠。</summary>
    private bool ServerOverlapInvalid(string id, Rect rect)
    {
        // 服务器：不得与任何其它节点相交，除非完全包含它；
        // 自身的托管子节点不参与校验（缩放时会被收紧到新矩形，不挡住缩放）
        if (IsServerNode(id))
        {
            foreach (var it in _allItems)
            {
                if (it.Id == id)
                    continue;
                if (_parentOf.TryGetValue(it.Id, out var parent) && parent == id)
                    continue;
                var r = new Rect(it.X, it.Y, it.Width, it.Height);
                if (rect.IntersectsWith(r) && !rect.Contains(r))
                    return true;
            }

            return false;
        }

        // 普通节点：不得与"其它"服务器相交（自己的父容器不算冲突，容器内可自由移动/缩放）
        _parentOf.TryGetValue(id, out var parentId);
        foreach (var it in _allItems)
        {
            if (it is not GraphBoxVm b || b.Id == parentId)
                continue;
            if (rect.IntersectsWith(new Rect(b.X, b.Y, b.Width, b.Height)))
                return true;
        }

        return false;
    }

    /// <summary>松开鼠标时校验调整结果，非法则还原到调整前；托管子节点夹回容器。</summary>
    public void ValidateResize(string id)
    {
        if (_dragSnapshot == null || !_geometry.TryGetValue(id, out var g))
        {
            CommitLayout();
            ClearDragState();
            return;
        }

        if (ServerOverlapInvalid(id, new Rect(g.X, g.Y, g.W, g.H)))
        {
            RejectDrag("调整后与服务器区域重叠，已还原");
            ClearDragState();
            return;
        }

        if (_parentOf.ContainsKey(id))
            ClampChild(id);

        CommitLayout();
        ClearDragState();
    }

    /// <summary>方向键移动选中节点（吸附 10px，含容器校验）。</summary>
    public void NudgeSelected(double dx, double dy)
    {
        if (_selectedId == null || !_geometry.TryGetValue(_selectedId, out _))
            return;

        BeginNodeDrag(_selectedId);
        MoveByDelta(_selectedId, dx, dy);

        if (_geometry.TryGetValue(_selectedId, out var g) &&
            ServerOverlapInvalid(_selectedId, new Rect(g.X, g.Y, g.W, g.H)))
        {
            RejectDrag("方向键移动被阻止：会与服务器区域重叠");
            ClearDragState();
            return;
        }

        if (_parentOf.ContainsKey(_selectedId))
            ClampChild(_selectedId);

        ClearDragState();
        CommitLayout();
    }

    /// <summary>独立节点若与服务器区块重叠（如解除 runsOn 后残留），移动到就近空白处并持久化。</summary>
    private void RelocateOrphanedStandalone()
    {
        if (Nodes.Count == 0 || Boxes.Count == 0)
            return;
        if (_synthetic)
            return;

        var serverRects = Boxes.Select(b => new Rect(b.X, b.Y, b.Width, b.Height)).ToArray();
        var obstacles = Boxes.Concat<GraphItemVm>(Chips).Concat(Nodes)
            .ToDictionary(i => i.Id, i => new Rect(i.X, i.Y, i.Width, i.Height));

        var changed = false;
        foreach (var node in Nodes)
        {
            var rect = new Rect(node.X, node.Y, node.Width, node.Height);
            if (!serverRects.Any(s => rect.IntersectsWith(s)))
                continue;

            var spot = FindFreeSpot(rect, node.Id, obstacles, serverRects);
            ApplyGeometry(node.Id, spot.X, spot.Y, node.Width, node.Height);
            obstacles[node.Id] = new Rect(spot.X, spot.Y, node.Width, node.Height);
            changed = true;
        }

        if (!changed)
            return;

        var layout = _layoutHistory.Get();
        layout.IsManual = true;
        foreach (var node in Nodes)
            layout.Nodes[node.Id] = new NodeLayout { X = node.X, Y = node.Y, Width = node.Width, Height = node.Height };
        _layoutHistory.Save(layout);
    }

    /// <summary>
    /// 手动布局下，**没有保存位置**的节点(典型: 新增的 SSH 服务器)会沿用默认堆叠坐标，
    /// 可能压到用户拖过的节点上。这里为这类节点在空白处重新找位置(服务器连同其托管子节点一起移动)并持久化，
    /// 使新增服务器在首次加载时即被放到不与任何节点重叠的位置。
    /// </summary>
    private void RelocateUnsavedItems()
    {
        if (_synthetic)
            return;

        var layout = _layoutHistory.Get();
        if (!layout.IsManual)
            return; // 全自动布局按列堆叠，不会重叠

        var all = Boxes.Concat<GraphItemVm>(Chips).Concat(Nodes).ToArray();
        var changed = false;

        foreach (var item in all)
        {
            if (layout.Nodes.ContainsKey(item.Id))
                continue; // 有保存位置：尊重用户摆放

            var rect = new Rect(item.X, item.Y, item.Width, item.Height);
            if (!OverlapsAny(item.Id, rect, all))
                continue;

            var obstacles = all.Where(o => o.Id != item.Id)
                .ToDictionary(o => o.Id, o => new Rect(o.X, o.Y, o.Width, o.Height));
            var serverRects = Boxes.Where(b => b.Id != item.Id)
                .Select(b => new Rect(b.X, b.Y, b.Width, b.Height)).ToList();

            var spot = FindFreeSpot(rect, item.Id, obstacles, serverRects);
            var dx = spot.X - item.X;
            var dy = spot.Y - item.Y;
            ApplyGeometry(item.Id, spot.X, spot.Y, item.Width, item.Height);

            // 服务器连同其托管子节点一起移动，保持嵌套
            if (item is GraphBoxVm && _childrenOf.TryGetValue(item.Id, out var children))
            {
                foreach (var childId in children)
                    if (_geometry.TryGetValue(childId, out var cg))
                        ApplyGeometry(childId, cg.X + dx, cg.Y + dy, cg.W, cg.H);
            }

            changed = true;
        }

        if (!changed)
            return;

        layout.IsManual = true;
        foreach (var i in all)
            layout.Nodes[i.Id] = new NodeLayout { X = i.X, Y = i.Y, Width = i.Width, Height = i.Height };
        _layoutHistory.Save(layout);
    }

    /// <summary>是否与其它节点重叠（排除自身、父容器、以及自己的托管子节点）。</summary>
    private bool OverlapsAny(string id, Rect rect, IReadOnlyList<GraphItemVm> all)
    {
        _parentOf.TryGetValue(id, out var parentId);
        foreach (var o in all)
        {
            if (o.Id == id || o.Id == parentId)
                continue;
            if (_parentOf.TryGetValue(o.Id, out var op) && op == id)
                continue; // 自己的托管子节点（本就在容器内）
            if (rect.IntersectsWith(new Rect(o.X, o.Y, o.Width, o.Height)))
                return true;
        }
        return false;
    }

    /// <summary>在服务器区块之外寻找最近的空白位置。</summary>
    private static Point FindFreeSpot(Rect rect, string selfId, IReadOnlyDictionary<string, Rect> obstacles, IReadOnlyList<Rect> serverRects)
    {
        var srv = serverRects.Where(s => rect.IntersectsWith(s)).OrderBy(s => s.Right).FirstOrDefault();
        var startX = srv.IsEmpty ? rect.X : srv.Right + 20;
        var startY = Math.Max(20, srv.IsEmpty ? rect.Y : srv.Y);

        bool Free(Rect r) =>
            !serverRects.Any(s => r.IntersectsWith(s)) &&
            !obstacles.Any(kv => kv.Key != selfId && r.IntersectsWith(kv.Value));

        for (var y = startY; y < startY + 4000; y += 20)
        {
            for (var x = startX; x < startX + 4000; x += 20)
            {
                var candidate = new Rect(x, y, rect.Width, rect.Height);
                if (Free(candidate))
                    return new Point(x, y);
            }
        }

        return new Point(startX, startY);
    }

    /// <summary>非法落点：恢复拖动前的几何。</summary>
    private void RejectDrag(string message)
    {
        if (_dragSnapshot != null)
        {
            _geometry.Clear();
            foreach (var kv in _dragSnapshot)
                _geometry[kv.Key] = kv.Value;

            foreach (var item in Boxes.Concat<GraphItemVm>(Chips).Concat(Nodes))
            {
                if (!_geometry.TryGetValue(item.Id, out var gg))
                    continue;
                item.X = gg.X;
                item.Y = gg.Y;
                item.Width = gg.W;
                item.Height = gg.H;
            }
        }

        if (_fastTimer is { IsEnabled: true })
            _fastTimer.Stop();
        _fastPending = false;

        UpdateSelectionRect();
        try
        {
            RebuildEdges(false);
        }
        catch
        {
            // 重算连线失败不影响回退结果
        }
        StatusMessage = message;
    }
    /// <summary>清除手动布局并重新自动布局。</summary>
    public void ResetLayout()
    {
        _layoutHistory.PushUndo();
        _layoutHistory.Clear();
        Load();
    }

    public bool CanUndo => _layoutHistory.CanUndo;
    public bool CanRedo => _layoutHistory.CanRedo;

    public void Undo()
    {
        if (!_layoutHistory.Undo())
            return;
        Load();
        StatusMessage = "已撤销";
    }

    public void Redo()
    {
        if (!_layoutHistory.Redo())
            return;
        Load();
        StatusMessage = "已重做";
    }

    // ---- 端口连线 ----

    public string? HitPort(Point point)
    {
        foreach (var port in Ports)
        {
            var bounds = port.Bounds;
            bounds.Inflate(2, 2);
            if (bounds.Contains(point))
                return port.Tag;
        }

        return null;
    }

    private Point PortPoint(string id, string side)
    {
        if (FindItem(id) is not { } item)
            return default;

        return TopologyGeometry.PortCenterFor(new Rect(item.X, item.Y, item.Width, item.Height), side);
    }

    public void StartLink(string id, string side)
    {
        if (IsDisabledNode(id))
        {
            StatusMessage = $"节点 {id} 所属服务器已禁用, 不允许建立连线。请先在服务器编辑里取消\"禁用\"。";
            return;
        }

        _linkFrom = (id, side);
        OnPropertyChanged(nameof(IsLinking));
        LinkGeometry = Freeze(new LineGeometry(PortPoint(id, side), PortPoint(id, side)));
    }

    public void UpdateLink(Point cursor)
    {
        if (_linkFrom is not { } from)
            return;
        LinkGeometry = Freeze(new LineGeometry(PortPoint(from.Id, from.Side), cursor));
    }

    public void CancelLink()
    {
        _linkFrom = null;
        LinkGeometry = Geometry.Empty;
        OnPropertyChanged(nameof(IsLinking));
    }

    public void FinishLink(Point drop) => AsyncOperation.Run("建立拓扑关系", async () =>
    {
        var from = _linkFrom;
        CancelLink();
        if (from is not { } source)
            return;

        var targetId = HitTest(drop);
        if (targetId == null || targetId == source.Id)
        {
            StatusMessage = "未落到目标节点, 已取消建边";
            return;
        }

        var type = InferType(source.Id, targetId);
        try
        {
            var config = await _configService.LoadConfigAsync();

            // 禁用的服务器不允许建链（拖线两端各查一次，用最新配置避免状态过期）
            var disabled = DisabledServerName(config, source.Id) ?? DisabledServerName(config, targetId);
            if (disabled != null)
            {
                StatusMessage = $"服务器 {disabled} 已禁用, 不允许建立连线。请先在服务器编辑里取消\"禁用\"并保存。";
                return;
            }

            if (!RelationRules.TryValidate(source.Id, targetId, type, config.Relations, out var error))
            {
                StatusMessage = $"不能建立该关系: {error}";
                return;
            }

            if (config.Relations.Any(r => r.From == source.Id && r.To == targetId && r.Type == type))
            {
                StatusMessage = "关系已存在";
                return;
            }

            config.Relations = config.Relations
                .Append(new RelationConfig { From = source.Id, To = targetId, Type = type })
                .ToArray();
            await _configService.SaveConfigAsync(config);
            StatusMessage = $"已添加关系: {source.Id} --{type}--> {targetId}";
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = $"添加关系失败: {ex.Message}";
        }
    });

    private static string InferType(string fromId, string toId)
    {
        var f = RelationRules.PrefixOf(fromId);
        var t = RelationRules.PrefixOf(toId);
        if ((f == "app" || f == "ds") && t == "ssh") return "runsOn";
        if (f == "app" && (t == "ds" || t == "app")) return "connectsTo";
        if (f == "ssh" && t == "ds") return "canAccess";
        return "relatedTo";
    }

    /// <summary>从拓扑节点 Info 里读"已禁用"标记（兼容内存 bool 与经序列化后的字符串）。</summary>
    private static bool IsNodeDisabled(TopologyNode node) =>
        node.Info.TryGetValue("disabled", out var v) &&
        (v is bool b ? b : v is string s && bool.TryParse(s, out var parsed) && parsed);

    private static void ApplyNodeHealth(GraphItemVm item, TopologyNode node)
    {
        var expired = TryGetBool(node.Info, "expired", out var e) && e;
        if (TryGetInt(node.Info, "daysRemaining", out var days) && days <= 0)
            expired = true;

        item.IsExpired = expired;
        item.IsExpiringSoon = !expired &&
            TryGetInt(node.Info, "daysRemaining", out var remaining) && remaining <= 30;
    }

    private static bool TryGetBool(Dictionary<string, object?>? info, string key, out bool value)
    {
        value = false;
        if (info == null || !info.TryGetValue(key, out var raw) || raw == null)
            return false;

        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case string s when bool.TryParse(s, out var parsed):
                value = parsed;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                value = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetInt(Dictionary<string, object?>? info, string key, out int value)
    {
        value = 0;
        if (info == null || !info.TryGetValue(key, out var raw) || raw == null)
            return false;

        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                value = (int)l;
                return true;
            case double d:
                value = (int)d;
                return true;
            case string s when int.TryParse(s, out var parsed):
                value = parsed;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    /// <summary>节点(服务器本身或其托管子节点)是否落在一台已禁用的服务器上。</summary>
    private bool IsDisabledNode(string id)
    {
        var sshId = RelationRules.PrefixOf(id) == "ssh"
            ? id
            : _parentOf.TryGetValue(id, out var parent) ? parent : null;
        return sshId != null && Boxes.Any(b => b.Id == sshId && b.IsDisabled);
    }

    /// <summary>按最新配置取节点所属服务器的名称，若该服务器已禁用；否则 null。</summary>
    private static string? DisabledServerName(AppConfig config, string nodeId)
    {
        if (RelationRules.PrefixOf(nodeId) != "ssh")
            return null;
        var server = config.Servers.FirstOrDefault(s => s.Id == nodeId["ssh:".Length..]);
        return server is { Disabled: true } ? server.Name : null;
    }

    // ---- 选边/删边 ----

    public GraphEdgeVm? HitEdge(Point point, double tolerance = 6)
    {
        foreach (var edge in Edges)
        {
            for (var i = 0; i + 1 < edge.Points.Count; i++)
            {
                if (DistanceToSegment(point, edge.Points[i], edge.Points[i + 1]) <= tolerance)
                    return edge;
            }
        }

        return null;
    }

    public void SelectEdge(GraphEdgeVm edge)
    {
        SelectNode(null);
        _selectedEdge = edge;
        _selEdgeOldType = edge.Type;
        SelectedEdgeType = edge.Type;
        SelectedEdgeNote = string.Empty;
        SelectedEdgeHint = edge.IsDiscovered
            ? "自动发现的关系可直接删除，不可编辑类型/备注。" +
              (string.IsNullOrWhiteSpace(edge.Evidence) ? string.Empty : $"\n证据: {edge.Evidence}") +
              (edge.IsDisputed
                  ? $"\n该关系与人工配置冲突，请核实后确认或删除。" +
                    (string.IsNullOrWhiteSpace(edge.DisputeReason) ? string.Empty : $"\n{edge.DisputeReason}")
                  : string.Empty)
            : edge.IsDerived
                ? $"由服务器/数据源运行环境或跳板/隧道配置推导，请在对应资产配置中修改。" +
                  (string.IsNullOrWhiteSpace(edge.Evidence) ? string.Empty : $"\n依据: {edge.Evidence}")
                : string.Empty;
        SelectedEdgeLine = edge.Line;
        OnPropertyChanged(nameof(HasSelectedEdge));
        OnPropertyChanged(nameof(SelectedEdgeSummary));
        OnPropertyChanged(nameof(SelectedEdgeEditable));
        OnPropertyChanged(nameof(SelectedEdgeDeletable));
        RebuildEdgeAnchors();

        if (!edge.IsDiscovered && !edge.IsDerived)
            LoadSelectedEdgeNote(edge);

        StatusMessage = $"已选关系: {edge.From} --{edge.Type}--> {edge.To}" +
                        (edge.IsDiscovered ? " (自动发现)" : edge.IsDerived ? " (配置推导)" : " (手动)");
    }

    private static string EffectiveRelationType(string? type) =>
        string.IsNullOrWhiteSpace(type) ? "relatedTo" : type;

    /// <summary>
    /// 在图边与配置 relation 之间做匹配：图边使用规范化节点 ID，配置里可能仍是原始 ID/别名，
    /// 因此先经 AssetCanonicalizer 解析再比较（否则删除/编辑会找不到原关系）。
    /// </summary>
    private static int FindRelationIndex(AppConfig config, string from, string to, string? type)
    {
        var relations = config.Relations ?? Array.Empty<RelationConfig>();
        var wantedType = EffectiveRelationType(type);
        AssetCanonicalization? canonical = null;
        try { canonical = AssetCanonicalizer.Build(config); }
        catch { /* 规范化失败时退回原始 ID 精确匹配 */ }

        for (var i = 0; i < relations.Length; i++)
        {
            var relation = relations[i];
            var relationFrom = canonical?.ResolveNodeId(relation.From) ?? relation.From;
            var relationTo = canonical?.ResolveNodeId(relation.To) ?? relation.To;
            if (string.Equals(relationFrom, from, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(relationTo, to, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(EffectiveRelationType(relation.Type), wantedType, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private void LoadSelectedEdgeNote(GraphEdgeVm edge) => AsyncOperation.Run("加载关系备注", async () =>
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var index = FindRelationIndex(config, edge.From, edge.To, edge.Type);
            if (ReferenceEquals(_selectedEdge, edge))
                SelectedEdgeNote = index >= 0 ? config.Relations[index].Note ?? string.Empty : string.Empty;
        }
        catch
        {
            // 读取备注失败忽略
        }
    });

    /// <summary>保存选中关系的类型与备注（仅手动关系）。</summary>
    public void SaveSelectedEdge() => AsyncOperation.Run("保存关系属性", async () =>
    {
        if (_selectedEdge is not { IsDiscovered: false, IsDerived: false } edge)
            return;

        try
        {
            var config = await _configService.LoadConfigAsync();
            var index = FindRelationIndex(config, edge.From, edge.To, _selEdgeOldType);
            if (index < 0)
            {
                SelectedEdgeHint = "原关系不存在，请刷新后重试。";
                return;
            }

            var relation = config.Relations[index];
            var others = config.Relations.Where((_, i) => i != index).ToArray();
            if (!RelationRules.TryValidate(relation.From, relation.To, SelectedEdgeType, others, out var error))
            {
                SelectedEdgeHint = $"不能保存: {error}";
                return;
            }

            config.Relations[index] = new RelationConfig
            {
                From = relation.From,
                To = relation.To,
                Type = SelectedEdgeType,
                Note = string.IsNullOrWhiteSpace(SelectedEdgeNote) ? null : SelectedEdgeNote.Trim()
            };
            await _configService.SaveConfigAsync(config);
            StatusMessage = "已更新关系属性";

            var (from, to, type) = (edge.From, edge.To, SelectedEdgeType);
            Load();
            var updated = Edges.FirstOrDefault(e => e.From == from && e.To == to && e.Type == type);
            if (updated != null)
                SelectEdge(updated);
            else
                ClearEdgeSelection();
        }
        catch (Exception ex)
        {
            SelectedEdgeHint = $"保存失败: {ex.Message}";
        }
    });

    public void ClearEdgeSelection()
    {
        _selectedEdge = null;
        SelectedEdgeLine = Geometry.Empty;
        EdgeAnchors.Clear();
        SelectedEdgeHint = string.Empty;
        OnPropertyChanged(nameof(HasSelectedEdge));
        OnPropertyChanged(nameof(SelectedEdgeSummary));
        OnPropertyChanged(nameof(SelectedEdgeEditable));
        OnPropertyChanged(nameof(SelectedEdgeDeletable));
    }

    /// <summary>选中发现边是否存在冲突标记（供面板显示确认按钮）。</summary>
    private void RebuildEdgeAnchors()
    {
        EdgeAnchors.Clear();
        if (_selectedEdge is not { } edge || edge.Points.Count < 2)
            return;

        const double size = 10;
        void Add(string tag, Point p)
        {
            var r = new Rect(p.X - size / 2, p.Y - size / 2, size, size);
            EdgeAnchors.Add(new GraphHandleVm { Tag = tag, Bounds = r, Shape = Freeze(new RectangleGeometry(r)) });
        }

        Add("af", edge.Points[0]);
        Add("at", edge.Points[^1]);
    }

    public string? HitEdgeAnchor(Point point)
    {
        foreach (var anchor in EdgeAnchors)
        {
            var bounds = anchor.Bounds;
            bounds.Inflate(3, 3);
            if (bounds.Contains(point))
                return anchor.Tag;
        }

        return null;
    }

    public void EndAnchorDrag(string tag, Point drop)
    {
        if (_selectedEdge is not { } edge)
            return;
        if (_synthetic)
        {
            StatusMessage = "压测图中不支持端点调整";
            return;
        }

        var nodeId = tag == "af" ? edge.From : edge.To;
        if (!_geometry.TryGetValue(nodeId, out var box))
            return;

        var side = NearestSide(box, drop);
        var layout = _layoutHistory.Get();
        layout.IsManual = true;
        var key = $"{edge.From}|{edge.Type}|{edge.To}";
        if (!layout.Edges.TryGetValue(key, out var edgeLayout))
            layout.Edges[key] = edgeLayout = new EdgeLayout();

        if (tag == "af")
            edgeLayout.From = new AnchorSpec { Side = side };
        else
            edgeLayout.To = new AnchorSpec { Side = side };

        _layoutHistory.Save(layout);
        RebuildEdges(false);

        var updated = Edges.FirstOrDefault(e => e.From == edge.From && e.To == edge.To && e.Type == edge.Type);
        if (updated != null)
            SelectEdge(updated);
        else
            ClearEdgeSelection();

        StatusMessage = $"已设置端点位置: {side}";
    }

    private static string NearestSide((double X, double Y, double W, double H) box, Point p)
    {
        var dl = Math.Abs(p.X - box.X);
        var dr = Math.Abs(p.X - (box.X + box.W));
        var dt = Math.Abs(p.Y - box.Y);
        var db = Math.Abs(p.Y - (box.Y + box.H));
        var min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        if (min == dl) return "left";
        if (min == dr) return "right";
        if (min == dt) return "top";
        return "bottom";
    }

    public void DeleteSelectedEdge() => AsyncOperation.Run("删除拓扑关系", async () =>
    {
        if (_selectedEdge is not { } edge)
            return;

        if (edge.IsDerived)
        {
            SelectedEdgeHint = "该关系由运行环境/跳板/隧道配置推导，不能作为人工关系删除；请到对应资产配置中修改。";
            StatusMessage = "该关系为配置推导，未删除。";
            return;
        }

        try
        {
            var config = await _configService.LoadConfigAsync();
            var index = FindRelationIndex(config, edge.From, edge.To, edge.Type);
            if (index < 0)
            {
                SelectedEdgeHint = "未在配置中找到对应的人工关系；该边可能由运行环境/跳板/隧道配置推导，请刷新后到资产配置中修改。";
                StatusMessage = "未找到可删除的人工关系。";
                return;
            }

            config.Relations = config.Relations.Where((_, i) => i != index).ToArray();
            await _configService.SaveConfigAsync(config);

            ClearEdgeSelection();
            StatusMessage = $"已删除关系: {edge.From} --{edge.Type}--> {edge.To}";
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除关系失败: {ex.Message}";
        }
    });

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var lengthSq = ab.LengthSquared;
        if (lengthSq < 1e-6)
            return (p - a).Length;

        var t = Math.Max(0, Math.Min(1, ((p - a) * ab) / lengthSq));
        return (p - (a + ab * t)).Length;
    }

    public void Discover(string serverIds) => AsyncOperation.Run("自动发现拓扑", async () =>
    {
        try
        {
            StatusMessage = "自动发现中...";
            var servers = string.IsNullOrWhiteSpace(serverIds)
                ? null
                : serverIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var result = await _topology.DiscoverAsync(servers, null);
            DiscoveryReport = BuildDiscoveryReport(result);
            var extra = result.Notes.Length + result.UnmatchedEndpoints.Count + result.DuplicateCandidates.Count > 0
                ? $"（说明 {result.Notes.Length} 条 / 疑似重复 {result.DuplicateCandidates.Count} 组 / 未匹配端点 {result.UnmatchedEndpoints.Count} 条，详见「更多操作 → 发现报告」）"
                : string.Empty;
            StatusMessage = $"发现完成: 新增 {result.NewEdges.Length} 条, 更新 {result.UpdatedEdges.Length} 条, 错误 {result.Errors.Length} 个{extra}";
            Load();
        }
        catch (DiscoveryInProgressException)
        {
            StatusMessage = "已有自动发现在执行，请稍后再试。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"自动发现失败: {ex.Message}";
        }
    });

    /// <summary>把选中的"待确认"节点确认为已登记资产（弹出对应新增窗口并预填），再把发现边重定向到新资产。</summary>
    private static void Add(Dictionary<string, List<TopologyNode>> map, string key, TopologyNode node)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = new List<TopologyNode>();
        list.Add(node);
    }

    private static bool IsNestedUnderHostedDocker(
        string nodeId,
        IReadOnlyDictionary<string, string> memberToDocker,
        IReadOnlyDictionary<string, string> hostedBy,
        IReadOnlyList<TopologyNode> servers) =>
        memberToDocker.TryGetValue(nodeId, out var dockerId) &&
        hostedBy.TryGetValue(dockerId, out var serverId) &&
        servers.Any(s => s.Id == serverId);

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    private static string LabelWithPort(TopologyNode node)
    {
        if (node.Info != null && node.Info.TryGetValue("port", out var port) && port != null)
        {
            var text = port.ToString();
            if (!string.IsNullOrEmpty(text))
                return $"{node.Label} :{text}";
        }

        return node.Label;
    }

    private static string BuildTooltip(TopologyNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{TypeLabel(node.Type)}: {node.Label}");
        sb.AppendLine($"ID: {node.Id}");

        var info = node.Info;
        AppendInfo(sb, info, "host", "主机");
        AppendInfo(sb, info, "port", "端口");
        AppendInfo(sb, info, "assetKey", "资产标识");
        AppendInfo(sb, info, "internalHost", "内网地址");
        AppendInfo(sb, info, "accessProfileCount", "接入方式");
        AppendInfo(sb, info, "aliases", "主机别名");
        AppendInfo(sb, info, "identityKinds", "身份类型");
        AppendInfo(sb, info, "ports", "监听端口");
        AppendInfo(sb, info, "username", "账号");
        AppendInfo(sb, info, "datasourceType", "数据库类型");
        AppendInfo(sb, info, "applicationType", "应用类型");
        AppendInfo(sb, info, "unit", "systemd 单元");
        AppendInfo(sb, info, "active", "运行状态");
        AppendInfo(sb, info, "load", "加载状态");
        AppendInfo(sb, info, "sub", "子状态");
        AppendInfo(sb, info, "image", "镜像");
        AppendInfo(sb, info, "state", "容器状态");
        AppendInfo(sb, info, "version", "版本");
        AppendInfo(sb, info, "deployedAt", "部署时间");
        AppendInfo(sb, info, "daysRemaining", "剩余天数");
        AppendInfo(sb, info, "expired", "已过期");
        AppendInfo(sb, info, "type", "类型");
        AppendInfo(sb, info, "path", "路径");
        AppendInfo(sb, info, "defaultDatabase", "默认库");
        AppendInfo(sb, info, "accessMode", "访问模式");
        AppendInfo(sb, info, "bind", "绑定地址");
        AppendInfo(sb, info, "process", "进程");
        AppendInfo(sb, info, "ssl", "SSL");
        AppendInfo(sb, info, "expiresAt", "到期时间");
        AppendInfo(sb, info, "lastVerifiedAt", "末次核实");
        AppendInfo(sb, info, "description", "描述");
        AppendInfo(sb, info, "tags", "标签");

        sb.Append("(密码等敏感信息不展示)");
        return sb.ToString();
    }

    private static void AppendInfo(StringBuilder sb, Dictionary<string, object?>? info, string key, string label)
    {
        if (info == null || !info.TryGetValue(key, out var value) || value == null)
            return;

        var text = FormatValue(value);
        if (!string.IsNullOrWhiteSpace(text))
            sb.AppendLine($"{label}: {text}");
    }

    private static string FormatValue(object value)
    {
        if (value is string s)
            return s;

        if (value is System.Collections.IEnumerable enumerable)
            return string.Join(", ", enumerable.Cast<object?>().Select(x => x?.ToString() ?? string.Empty));

        return value.ToString() ?? string.Empty;
    }

    private static string TypeLabel(string type) => type switch
    {
        "ssh" => "SSH服务器",
        "datasource" => "数据库",
        "application" => "应用",
        "service" => "系统服务",
        "container" => "容器",
        "deploy" => "部署",
        "system" => "业务系统",
        "domain" => "域名",
        "port" => "端口",
        "cert" => "TLS证书",
        "mq" => "消息队列",
        _ => type
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>旧版拓扑关系类型下拉项。</summary>
public sealed record RelationTypeOption(string Value, string Label);
