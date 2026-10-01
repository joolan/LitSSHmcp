using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.ViewModels;

public class GraphBoxVm
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string SubLabel { get; set; } = string.Empty;
    public string Tooltip { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class GraphChipVm
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Tooltip { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class GraphNodeVm
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Tooltip { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class GraphEdgeVm
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double LabelX { get; set; }
    public double LabelY { get; set; }
    public bool IsDiscovered { get; set; }
}

public class TopologyViewModel : INotifyPropertyChanged
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

    private readonly IConfigService _configService = AppServiceFactory.CreateConfigService();
    private readonly ITopologyService _topology;
    private string _statusMessage = string.Empty;
    private double _canvasWidth = 940;
    private double _canvasHeight = 700;

    public TopologyViewModel()
    {
        _topology = AppServiceFactory.CreateTopologyService(_configService);
        Load();
    }

    public ObservableCollection<GraphBoxVm> Boxes { get; } = new();
    public ObservableCollection<GraphChipVm> Chips { get; } = new();
    public ObservableCollection<GraphNodeVm> Nodes { get; } = new();
    public ObservableCollection<GraphEdgeVm> Edges { get; } = new();

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

    public async void Load()
    {
        try
        {
            var graph = await _topology.GetGraphAsync();

            Boxes.Clear();
            Chips.Clear();
            Nodes.Clear();
            Edges.Clear();

            var geometry = new Dictionary<string, (double X, double Y, double W, double H)>(StringComparer.Ordinal);
            var labelById = new Dictionary<string, string>(StringComparer.Ordinal);

            var servers = graph.Nodes.Where(n => n.Type == "ssh").ToArray();
            var apps = graph.Nodes.Where(n => n.Type == "application").ToArray();
            var datasources = graph.Nodes.Where(n => n.Type != "ssh" && n.Type != "application").ToArray();

            foreach (var node in graph.Nodes)
                labelById[node.Id] = LabelWithPort(node);

            var tooltipById = graph.Nodes.ToDictionary(n => n.Id, BuildTooltip);

            // runsOn: 被托管节点 -> 服务器（应用与数据库均可 runsOn）
            var hostedBy = graph.Edges
                .Where(e => e.Type == "runsOn")
                .GroupBy(e => e.From)
                .ToDictionary(g => g.Key, g => g.First().To);

            var hostedApps = new Dictionary<string, List<TopologyNode>>(StringComparer.Ordinal);
            var hostedDs = new Dictionary<string, List<TopologyNode>>(StringComparer.Ordinal);
            var standaloneApps = new List<TopologyNode>();
            var standaloneDs = new List<TopologyNode>();

            foreach (var app in apps)
            {
                if (hostedBy.TryGetValue(app.Id, out var sid) && servers.Any(s => s.Id == sid))
                    Add(hostedApps, sid, app);
                else
                    standaloneApps.Add(app);
            }

            foreach (var ds in datasources)
            {
                if (hostedBy.TryGetValue(ds.Id, out var sid) && servers.Any(s => s.Id == sid))
                    Add(hostedDs, sid, ds);
                else
                    standaloneDs.Add(ds);
            }

            // 左列：服务器区块（内嵌托管的 应用/数据库）
            double serverY = 20;
            foreach (var server in servers)
            {
                var appsHere = hostedApps.TryGetValue(server.Id, out var a) ? a : new List<TopologyNode>();
                var dsHere = hostedDs.TryGetValue(server.Id, out var d) ? d : new List<TopologyNode>();
                var children = appsHere.Concat(dsHere).ToArray();

                var chipArea = children.Length == 0 ? 24 : children.Length * ChipHeight + (children.Length - 1) * ChipGap;
                var boxHeight = ServerHeader + chipArea + 12;

                var sub = children.Length == 0
                    ? "(无托管资产)"
                    : $"{appsHere.Count} 应用 / {dsHere.Count} 数据库 (runsOn)";

                Boxes.Add(new GraphBoxVm
                {
                    Id = server.Id,
                    Label = labelById[server.Id],
                    SubLabel = sub,
                    Tooltip = tooltipById[server.Id],
                    X = ServerX,
                    Y = serverY,
                    Width = ServerWidth,
                    Height = boxHeight
                });
                geometry[server.Id] = (ServerX, serverY, ServerWidth, boxHeight);

                for (var i = 0; i < children.Length; i++)
                {
                    var chipY = serverY + ServerHeader + i * (ChipHeight + ChipGap);
                    var child = children[i];
                    Chips.Add(new GraphChipVm
                    {
                        Id = child.Id,
                        Label = labelById[child.Id],
                        Type = child.Type,
                        Tooltip = tooltipById[child.Id],
                        X = ServerX + 12,
                        Y = chipY,
                        Width = ServerWidth - 24,
                        Height = ChipHeight
                    });
                    geometry[child.Id] = (ServerX + 12, chipY, ServerWidth - 24, ChipHeight);
                }

                serverY += boxHeight + 18;
            }

            // 中列：未托管的应用
            double appY = 20;
            foreach (var app in standaloneApps)
            {
                Nodes.Add(new GraphNodeVm
                {
                    Id = app.Id,
                    Label = labelById[app.Id],
                    Type = "application",
                    Tooltip = tooltipById[app.Id],
                    X = StandaloneAppX,
                    Y = appY,
                    Width = StandaloneAppWidth,
                    Height = StandaloneAppHeight
                });
                geometry[app.Id] = (StandaloneAppX, appY, StandaloneAppWidth, StandaloneAppHeight);
                appY += StandaloneAppHeight + 12;
            }

            // 右列：未托管的数据库
            double dsY = 20;
            foreach (var ds in standaloneDs)
            {
                Nodes.Add(new GraphNodeVm
                {
                    Id = ds.Id,
                    Label = labelById[ds.Id],
                    Type = "datasource",
                    Tooltip = tooltipById[ds.Id],
                    X = DsX,
                    Y = dsY,
                    Width = DsWidth,
                    Height = DsHeight
                });
                geometry[ds.Id] = (DsX, dsY, DsWidth, DsHeight);
                dsY += DsHeight + 12;
            }

            // 边：connectsTo(应用->数据库) 与 canAccess(服务器->数据库)
            foreach (var edge in graph.Edges)
            {
                if (edge.Type == "runsOn")
                    continue;

                // 数据库就运行在该服务器上时，canAccess 属于冗余，省略
                if (edge.Type == "canAccess" && hostedBy.TryGetValue(edge.To, out var host) && host == edge.From)
                    continue;

                if (!geometry.TryGetValue(edge.From, out var from) ||
                    !geometry.TryGetValue(edge.To, out var to))
                    continue;

                var x1 = from.X + from.W;
                var y1 = from.Y + from.H / 2;
                var x2 = to.X;
                var y2 = to.Y + to.H / 2;

                Edges.Add(new GraphEdgeVm
                {
                    X1 = x1,
                    Y1 = y1,
                    X2 = x2,
                    Y2 = y2,
                    Type = edge.Type,
                    Label = edge.Type,
                    LabelX = (x1 + x2) / 2 - 24,
                    LabelY = (y1 + y2) / 2 - 8,
                    IsDiscovered = string.Equals(edge.Source, "discovered", StringComparison.OrdinalIgnoreCase)
                });
            }

            CanvasHeight = Math.Max(Math.Max(serverY, appY), dsY) + 40;
            CanvasWidth = DsX + DsWidth + 40;
            StatusMessage = $"服务器 {servers.Length} 台, 应用 {apps.Length} 个, 数据库 {datasources.Length} 个, 连线 {Edges.Count} 条 (runsOn 以嵌套展示)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载拓扑失败: {ex.Message}";
        }
    }

    public async void Discover(string serverIds)
    {
        try
        {
            StatusMessage = "自动发现中...";
            var servers = string.IsNullOrWhiteSpace(serverIds)
                ? null
                : serverIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var result = await _topology.DiscoverAsync(servers, null);
            StatusMessage = $"发现完成: 新增 {result.NewEdges.Length} 条, 更新 {result.UpdatedEdges.Length} 条, 错误 {result.Errors.Length} 个";
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = $"自动发现失败: {ex.Message}";
        }
    }

    private static void Add(Dictionary<string, List<TopologyNode>> map, string key, TopologyNode node)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = new List<TopologyNode>();
        list.Add(node);
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
        AppendInfo(sb, info, "username", "账号");
        AppendInfo(sb, info, "datasourceType", "数据库类型");
        AppendInfo(sb, info, "applicationType", "应用类型");
        AppendInfo(sb, info, "defaultDatabase", "默认库");
        AppendInfo(sb, info, "accessMode", "访问模式");
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
        _ => type
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
