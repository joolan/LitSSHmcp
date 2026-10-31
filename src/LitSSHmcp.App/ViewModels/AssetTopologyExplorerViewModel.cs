using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.ViewModels;

public sealed class AssetTopologyExplorerViewModel : INotifyPropertyChanged
{
    private static readonly HashSet<string> DependencyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "connectsTo", "canAccess", "exposes", "accessedVia", "upstreamOf", "relatedTo"
    };

    private static readonly HashSet<string> LowLevelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "service", "container", "deploy", "port", "cert", "system"
    };

    private readonly ITopologyService _topology;
    private readonly IAssetConfigurationSuggestionService? _suggestionsService;
    private readonly IAssetConfigurationConsistencyService? _consistencyService;
    private TopologyGraph? _graph;
    private Dictionary<string, TopologyNode> _nodeById = new(StringComparer.Ordinal);
    private List<ServerLaneRow> _allLanes = new();
    private List<AssetRow> _allEntries = new();
    private List<DependencyRow> _allDependencies = new();

    private string _searchText = string.Empty;
    private bool _showLowLevel;
    private bool _showAllDependencies;
    private string _statusMessage = "正在加载结构化拓扑...";
    private string _summary = string.Empty;
    private string _selectedTitle = "未选择资产";
    private string _selectedSubtitle = "点击左侧服务器、应用或数据源查看详情";
    private string _selectedDetail = string.Empty;
    private string _selectedEvidence = string.Empty;
    private bool _showDiscoveryEvidence;
    private string? _selectedAssetId;
    private AssetRow? _selectedFlowTarget;
    private string _flowSummary = "选择一个应用或域名查看链路";
    private string _matrixSummary = string.Empty;

    public AssetTopologyExplorerViewModel(
        ITopologyService topology,
        IAssetConfigurationSuggestionService? suggestionsService = null,
        IAssetConfigurationConsistencyService? consistencyService = null)
    {
        _topology = topology;
        _suggestionsService = suggestionsService;
        _consistencyService = consistencyService;
        RefreshCommand = new RelayCommand(_ => Load());
        LoadSuggestionsCommand = new RelayCommand(_ => LoadSuggestions());
        RunConfigurationCheckCommand = new RelayCommand(_ => RunConfigurationCheck());
        ApplyHighConfidenceSuggestionsCommand = new RelayCommand(_ => ApplyHighConfidenceSuggestions());
        ApplySuggestionCommand = new RelayCommand(parameter =>
        {
            if (parameter is SuggestionRow row)
                ApplySuggestion(row);
        });
        SelectCommand = new RelayCommand(parameter =>
        {
            if (parameter is AssetRow asset)
                Select(asset);
        });
    }

    public ObservableCollection<ServerLaneRow> Lanes { get; } = new();
    public ObservableCollection<AssetRow> EntryAssets { get; } = new();
    public ObservableCollection<DependencyRow> Dependencies { get; } = new();
    public ObservableCollection<string> SelectedRelations { get; } = new();
    public ObservableCollection<AssetRow> FlowTargets { get; } = new();
    public ObservableCollection<FlowGroupRow> FlowGroups { get; } = new();
    public ObservableCollection<FlowPathRow> FlowPaths { get; } = new();
    public ObservableCollection<string> MatrixColumns { get; } = new();
    public ObservableCollection<MatrixRow> MatrixRows { get; } = new();
    public ObservableCollection<SuggestionRow> Suggestions { get; } = new();
    public ObservableCollection<IssueRow> Issues { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand LoadSuggestionsCommand { get; }
    public ICommand RunConfigurationCheckCommand { get; }
    public ICommand ApplyHighConfidenceSuggestionsCommand { get; }
    public ICommand ApplySuggestionCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value))
                return;
            ApplyFilter();
        }
    }

    public bool ShowLowLevel
    {
        get => _showLowLevel;
        set
        {
            if (!Set(ref _showLowLevel, value))
                return;
            BuildView();
        }
    }

    public bool ShowAllDependencies
    {
        get => _showAllDependencies;
        set
        {
            if (!Set(ref _showAllDependencies, value))
                return;
            ApplyFilter();
            BuildMatrix();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string SelectedTitle
    {
        get => _selectedTitle;
        private set => Set(ref _selectedTitle, value);
    }

    public string SelectedSubtitle
    {
        get => _selectedSubtitle;
        private set => Set(ref _selectedSubtitle, value);
    }

    public string SelectedDetail
    {
        get => _selectedDetail;
        private set => Set(ref _selectedDetail, value);
    }

    public string SelectedEvidence
    {
        get => _selectedEvidence;
        private set => Set(ref _selectedEvidence, value);
    }

    public bool ShowDiscoveryEvidence
    {
        get => _showDiscoveryEvidence;
        set
        {
            if (!Set(ref _showDiscoveryEvidence, value))
                return;
            _ = RefreshSelectedEvidenceAsync();
        }
    }

    public AssetRow? SelectedFlowTarget
    {
        get => _selectedFlowTarget;
        set
        {
            if (!Set(ref _selectedFlowTarget, value))
                return;
            BuildFlow();
        }
    }

    public string FlowSummary
    {
        get => _flowSummary;
        private set => Set(ref _flowSummary, value);
    }

    public string MatrixSummary
    {
        get => _matrixSummary;
        private set => Set(ref _matrixSummary, value);
    }

    public void Load() => AsyncOperation.Run("加载结构化拓扑", LoadAsync);

    public async Task LoadAsync()
    {
        try
        {
            StatusMessage = "正在加载结构化拓扑...";
            _graph = await _topology.GetCuratedGraphAsync();
            _nodeById = _graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            BuildView();
            StatusMessage = $"已加载 {_graph.Nodes.Length} 个资产节点、{_graph.Edges.Length} 条关系";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
    }

    private void BuildView()
    {
        _allLanes.Clear();
        _allEntries.Clear();
        _allDependencies.Clear();
        if (_graph is null)
            return;

        var runsOn = _graph.Edges
            .Where(e => e.Type == "runsOn")
            .Select(e => (e.From, e.To))
            .ToLookup(x => x.To, x => x.From, StringComparer.Ordinal);
        var childIds = _graph.Edges
            .Where(e => e.Type == "runsOn")
            .Select(e => e.From)
            .ToHashSet(StringComparer.Ordinal);

        var servers = _graph.Nodes
            .Where(n => n.Type == "ssh")
            .OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var server in servers)
        {
            var children = runsOn[server.Id]
                .Select(id => _nodeById.GetValueOrDefault(id))
                .Where(n => n is not null && IsVisible(n))
                .Select(n => ToAsset(n!))
                .OrderBy(a => TypeOrder(a.Kind))
                .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _allLanes.Add(new ServerLaneRow(
                ToAsset(server),
                children,
                BuildLaneSummary(children)));
        }

        var orphanChildren = _graph.Nodes
            .Where(n => n.Type != "ssh" && n.Type != "domain" && !childIds.Contains(n.Id) && IsVisible(n))
            .Select(ToAsset)
            .OrderBy(a => TypeOrder(a.Kind))
            .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (orphanChildren.Length > 0)
        {
            _allLanes.Add(new ServerLaneRow(
                new AssetRow
                {
                    Id = "asset:unassigned",
                    Label = "未归属 / 独立资产",
                    Kind = "group",
                    KindLabel = "未归属",
                    Subtitle = $"{orphanChildren.Length} 个资产",
                    Detail = "没有 runsOn 关系，或不在当前服务器范围内。"
                },
                orphanChildren,
                BuildLaneSummary(orphanChildren)));
        }

        foreach (var domain in _graph.Nodes.Where(n => n.Type == "domain").OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase))
        {
            var targets = _graph.Edges
                .Where(e => e.From == domain.Id && e.Type is "exposes" or "resolvesTo")
                .Select(e => _nodeById.GetValueOrDefault(e.To))
                .Where(n => n is not null)
                .Select(n => n!.Label)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var asset = ToAsset(domain);
            asset.Detail = targets.Length == 0
                ? asset.Detail
                : asset.Detail + Environment.NewLine + "→ " + string.Join(" / ", targets);
            _allEntries.Add(asset);
        }

        if (_showLowLevel)
        {
            foreach (var node in _graph.Nodes.Where(n => n.Type == "cert" || n.Type == "port"))
            {
                var asset = ToAsset(node);
                _allEntries.Add(asset);
            }
        }

        foreach (var edge in _graph.Edges.Where(e => DependencyTypes.Contains(e.Type)))
        {
            var from = _nodeById.GetValueOrDefault(edge.From);
            var to = _nodeById.GetValueOrDefault(edge.To);
            if (from is null || to is null)
                continue;

            var cross = IsCrossServer(edge.From, edge.To);
            _allDependencies.Add(new DependencyRow
            {
                FromId = edge.From,
                ToId = edge.To,
                FromLabel = from.Label,
                ToLabel = to.Label,
                Relation = edge.Type,
                IsCrossServer = cross,
                Detail = edge.Evidence ?? string.Empty
            });
        }

        _allDependencies = _allDependencies
            .OrderByDescending(d => d.IsCrossServer)
            .ThenBy(d => d.FromLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Relation, StringComparer.Ordinal)
            .ThenBy(d => d.ToLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Summary = $"服务器 {servers.Length} 台 · 入口 {_allEntries.Count} 个 · 关系 {_allDependencies.Count} 条";
        ApplyFilter();
        BuildFlowTargets();
        BuildMatrix();
    }

    private void BuildFlowTargets()
    {
        var previousId = _selectedFlowTarget?.Id;
        FlowTargets.Clear();
        if (_graph is null)
            return;

        foreach (var node in _graph.Nodes
                     .Where(n => n.Type is "application" or "domain")
                     .OrderBy(n => n.Type == "domain" ? 0 : 1)
                     .ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase))
        {
            FlowTargets.Add(ToAsset(node));
        }

        var next = FlowTargets.FirstOrDefault(a => a.Id == previousId) ?? FlowTargets.FirstOrDefault();
        if (Set(ref _selectedFlowTarget, next, nameof(SelectedFlowTarget)))
            BuildFlow();
    }

    private void BuildFlow()
    {
        FlowGroups.Clear();
        FlowPaths.Clear();
        if (_graph is null || SelectedFlowTarget is null ||
            !_nodeById.TryGetValue(SelectedFlowTarget.Id, out var root))
        {
            FlowSummary = "选择一个应用或域名查看链路";
            return;
        }

        var highlighted = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        BuildFlowPaths(root.Id, highlighted);

        var upstream = _graph.Edges
            .Where(e => e.To == root.Id && !IsContainment(e.Type))
            .Select(e => e.From)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var downstream = _graph.Edges
            .Where(e => e.From == root.Id && !IsContainment(e.Type) && e.Type != "resolvesTo")
            .Select(e => e.To)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        AddFlowGroup("上游 / 入口", upstream, highlighted);
        AddFlowGroup("当前资产", new[] { root.Id }, highlighted);
        AddFlowGroup("下游 / 依赖", downstream, highlighted);

        var host = HostOf(root.Id);
        if (!string.IsNullOrWhiteSpace(host))
            AddFlowGroup("运行宿主", new[] { host }, highlighted);

        FlowSummary = $"上游 {upstream.Length} 个 · 下游 {downstream.Length} 个 · " +
                      (string.IsNullOrWhiteSpace(host) ? "宿主未知" : $"宿主 {_nodeById.GetValueOrDefault(host)?.Label ?? host}") +
                      $" · 路径 {FlowPaths.Count} 条";
    }

    private void BuildFlowPaths(string rootId, HashSet<string> highlighted)
    {
        if (_graph is null)
            return;

        const int maxDepth = 4;
        const int maxPaths = 40;
        var added = 0;

        void Walk(string current, List<TopologyEdge> path, HashSet<string> visited, bool forward, string direction)
        {
            if (added >= maxPaths || path.Count >= maxDepth)
                return;

            var edges = forward
                ? _graph.Edges.Where(e => e.From == current && IsPathEdge(e))
                : _graph.Edges.Where(e => e.To == current && IsPathEdge(e));

            foreach (var edge in edges)
            {
                var next = forward ? edge.To : edge.From;
                if (string.Equals(next, current, StringComparison.Ordinal) || !visited.Add(next))
                    continue;

                path.Add(edge);
                highlighted.Add(next);
                AddFlowPath(path, direction);
                added++;
                Walk(next, path, visited, forward, direction);
                path.RemoveAt(path.Count - 1);
                visited.Remove(next);

                if (added >= maxPaths)
                    break;
            }
        }

        Walk(rootId, new List<TopologyEdge>(), new HashSet<string>(StringComparer.Ordinal) { rootId }, true, "下游");
        Walk(rootId, new List<TopologyEdge>(), new HashSet<string>(StringComparer.Ordinal) { rootId }, false, "上游");
    }

    private void AddFlowPath(IReadOnlyList<TopologyEdge> edges, string direction)
    {
        if (edges.Count == 0)
            return;

        var builder = new StringBuilder();
        if (direction == "下游")
        {
            builder.Append(Label(edges[0].From));
            foreach (var edge in edges)
                builder.Append($" -{edge.Type}-> {Label(edge.To)}");
        }
        else
        {
            builder.Append(Label(edges[0].To));
            foreach (var edge in edges)
                builder.Append($" <-{edge.Type}- {Label(edge.From)}");
        }

        FlowPaths.Add(new FlowPathRow(direction, builder.ToString(), edges.Count));
    }

    private void AddFlowGroup(string title, IEnumerable<string> ids, HashSet<string> highlighted)
    {
        var nodes = ids
            .Select(id => _nodeById.GetValueOrDefault(id))
            .Where(n => n is not null)
            .Select(n => ToAsset(n!))
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(a => TypeOrder(a.Kind))
            .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var node in nodes)
            node.IsHighlighted = highlighted.Contains(node.Id);

        if (nodes.Length > 0)
            FlowGroups.Add(new FlowGroupRow(title, nodes));
    }

    private static bool IsPathEdge(TopologyEdge edge) =>
        !IsContainment(edge.Type);

    private void BuildMatrix()
    {
        MatrixColumns.Clear();
        MatrixRows.Clear();
        MatrixSummary = "暂无服务器";

        if (_graph is null)
            return;

        var servers = _graph.Nodes
            .Where(n => n.Type == "ssh")
            .OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (servers.Length == 0)
            return;

        var index = servers
            .Select((server, i) => (server.Id, Index: i))
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.Ordinal);
        var counts = new int[servers.Length, servers.Length];

        foreach (var dependency in _allDependencies)
        {
            if (!ShowAllDependencies && !dependency.IsCrossServer)
                continue;

            var fromHost = HostOf(dependency.FromId);
            var toHost = HostOf(dependency.ToId);
            if (fromHost is null || toHost is null ||
                !index.TryGetValue(fromHost, out var from) ||
                !index.TryGetValue(toHost, out var to) ||
                (!ShowAllDependencies && from == to))
                continue;

            counts[from, to]++;
        }

        foreach (var server in servers)
            MatrixColumns.Add(server.Label);

        for (var i = 0; i < servers.Length; i++)
        {
            var cells = new List<MatrixCell>();
            for (var j = 0; j < servers.Length; j++)
            {
                var count = counts[i, j];
                cells.Add(new MatrixCell
                {
                    Text = count > 0 ? count.ToString() : "·",
                    HasValue = count > 0
                });
            }
            MatrixRows.Add(new MatrixRow(servers[i].Label, cells));
        }

        var total = counts.Cast<int>().Sum();
        MatrixSummary = $"服务器 {servers.Length} 台 · {(ShowAllDependencies ? "全部" : "跨服务器")}关系 {total} 条";
    }

    private static bool IsContainment(string type) =>
        type is "runsOn" or "hostedOn" or "listensOn";

    private void ApplyFilter()
    {
        Lanes.Clear();
        EntryAssets.Clear();
        Dependencies.Clear();

        var search = (SearchText ?? string.Empty).Trim();
        bool Matches(AssetRow asset) =>
            search.Length == 0 ||
            asset.Label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            asset.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            asset.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase);

        foreach (var lane in _allLanes)
        {
            var children = search.Length == 0
                ? lane.Children
                : Matches(lane.Server)
                    ? lane.Children
                    : lane.Children.Where(Matches).ToArray();

            if (search.Length > 0 && !Matches(lane.Server) && children.Count == 0)
                continue;

            Lanes.Add(new ServerLaneRow(lane.Server, children, BuildLaneSummary(children)));
        }

        foreach (var entry in _allEntries.Where(Matches))
            EntryAssets.Add(entry);

        foreach (var dependency in _allDependencies)
        {
            if (!ShowAllDependencies && !dependency.IsCrossServer)
                continue;
            if (search.Length > 0 &&
                !dependency.FromLabel.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !dependency.ToLabel.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !dependency.Relation.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            Dependencies.Add(dependency);
        }
    }

    private void LoadSuggestions() => AsyncOperation.Run("扫描配置建议", async () =>
    {
        await LoadSuggestionsCoreAsync();
    });

    private async Task LoadSuggestionsCoreAsync()
    {
        if (_suggestionsService is null)
        {
            StatusMessage = "配置建议服务不可用";
            return;
        }

        try
        {
            StatusMessage = "正在扫描配置建议...";
            var suggestions = await _suggestionsService.GetSuggestionsAsync();
            Suggestions.Clear();
            foreach (var suggestion in suggestions)
                Suggestions.Add(new SuggestionRow(suggestion));
            StatusMessage = $"配置建议: {Suggestions.Count} 条";
        }
        catch (Exception ex)
        {
            StatusMessage = $"配置建议扫描失败: {ex.Message}";
        }
    }

    private void ApplySuggestion(SuggestionRow row) => AsyncOperation.Run("应用配置建议", async () =>
    {
        if (_suggestionsService is null)
            return;

        try
        {
            if (!await _suggestionsService.ApplyAsync(row.Suggestion))
            {
                StatusMessage = "应用建议失败：资产已不存在或建议类型不支持";
                return;
            }

            StatusMessage = $"已应用建议: {row.Title}";
            await LoadAsync();
            await LoadSuggestionsCoreAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"应用建议失败: {ex.Message}";
        }
    });

    private void ApplyHighConfidenceSuggestions() => AsyncOperation.Run("批量应用配置建议", async () =>
    {
        if (_suggestionsService is null)
            return;

        try
        {
            var candidates = Suggestions
                .Where(s => s.Suggestion.Confidence >= 0.85)
                .Select(s => s.Suggestion)
                .ToList();
            if (candidates.Count == 0)
            {
                StatusMessage = "没有置信度 ≥ 85% 的配置建议";
                return;
            }

            var applied = 0;
            foreach (var suggestion in candidates)
            {
                if (await _suggestionsService.ApplyAsync(suggestion))
                    applied++;
            }

            await LoadAsync();
            await LoadSuggestionsCoreAsync();
            StatusMessage = $"已批量应用 {applied}/{candidates.Count} 条高置信度建议";
        }
        catch (Exception ex)
        {
            StatusMessage = $"批量应用建议失败: {ex.Message}";
        }
    });

    private void RunConfigurationCheck() => AsyncOperation.Run("配置体检", async () =>
    {
        if (_consistencyService is null)
        {
            StatusMessage = "配置体检服务不可用";
            return;
        }

        try
        {
            StatusMessage = "正在对照发现证据检查配置...";
            var issues = await _consistencyService.GetIssuesAsync();
            Issues.Clear();
            foreach (var issue in issues)
                Issues.Add(new IssueRow(issue));
            StatusMessage = $"配置体检: {Issues.Count} 条问题";
        }
        catch (Exception ex)
        {
            StatusMessage = $"配置体检失败: {ex.Message}";
        }
    });

    private void Select(AssetRow asset)
    {
        _selectedAssetId = asset.Id;
        SelectedTitle = asset.Label;
        SelectedSubtitle = $"{asset.KindLabel} · {asset.Id}";
        SelectedDetail = asset.Detail;
        SelectedRelations.Clear();

        if (_graph is null || !_nodeById.TryGetValue(asset.Id, out var node))
        {
            SelectedEvidence = string.Empty;
            return;
        }

        foreach (var edge in _graph.Edges.Where(e => e.From == node.Id || e.To == node.Id))
        {
            var from = _nodeById.GetValueOrDefault(edge.From)?.Label ?? edge.From;
            var to = _nodeById.GetValueOrDefault(edge.To)?.Label ?? edge.To;
            var evidence = string.IsNullOrWhiteSpace(edge.Evidence) ? string.Empty : $"  ({edge.Evidence})";
            SelectedRelations.Add($"{from}  -{edge.Type}->  {to}{evidence}");
        }

        if (SelectedRelations.Count == 0)
            SelectedRelations.Add("暂无关系");

        _ = RefreshSelectedEvidenceAsync();
    }

    private async Task RefreshSelectedEvidenceAsync()
    {
        var assetId = _selectedAssetId;
        if (!ShowDiscoveryEvidence || string.IsNullOrWhiteSpace(assetId) ||
            _graph is null || !_nodeById.TryGetValue(assetId, out var node) || node.Type != "ssh")
        {
            SelectedEvidence = string.Empty;
            return;
        }

        try
        {
            var serverId = InfoStrings(node, "profileIds").FirstOrDefault() ?? StripPrefix(node.Id);
            var evidence = await _topology.GetServerDiscoveryEvidenceAsync(serverId);
            if (!string.Equals(_selectedAssetId, assetId, StringComparison.Ordinal))
                return;

            SelectedEvidence = FormatEvidence(evidence);
        }
        catch (Exception ex)
        {
            if (string.Equals(_selectedAssetId, assetId, StringComparison.Ordinal))
                SelectedEvidence = $"发现证据加载失败: {ex.Message}";
        }
    }

    private static string FormatEvidence(ServerDiscoveryEvidence? evidence)
    {
        if (evidence is null)
            return "暂无发现证据（可先对服务器执行一次快照）";

        var sb = new StringBuilder();
        sb.AppendLine($"最近快照: {evidence.CapturedAt ?? "未知"} #{evidence.SnapshotId}");

        if (evidence.Ports.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("监听端口:");
            foreach (var port in evidence.Ports.OrderBy(p => p.Port))
            {
                var managed = port.SystemdManaged
                    ? $"systemd: {port.Unit}"
                    : "手工/未识别管理方式";
                sb.AppendLine($"  {port.Protocol}/{port.Port}  {port.Process}  [{managed}]" +
                              (string.IsNullOrWhiteSpace(port.Executable) ? string.Empty : $"  {port.Executable}"));
            }
        }

        if (evidence.Services.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("系统服务:");
            foreach (var service in evidence.Services.Take(30))
                sb.AppendLine($"  {service.Unit}  {service.Active}/{service.Sub}  {service.Description}");
        }

        if (evidence.Containers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("容器:");
            foreach (var container in evidence.Containers.Take(30))
                sb.AppendLine($"  {container.Name}  {container.Image}  {container.State}  {container.Ports}");
        }

        if (evidence.Domains.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Nginx 站点 / 证书:");
            foreach (var domain in evidence.Domains.Take(30))
            {
                sb.AppendLine($"  {domain.Domain}  ssl={domain.Ssl}  ports={string.Join(",", domain.Ports)}");
                foreach (var cert in domain.Certificates)
                    sb.AppendLine($"    证书 {cert.SubjectCn ?? cert.Path}  到期 {cert.ExpiresAt}  指纹 {cert.Fingerprint}");
            }
        }

        if (evidence.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("说明:");
            foreach (var note in evidence.Notes)
                sb.AppendLine("  " + note);
        }

        return sb.ToString().TrimEnd();
    }

    private static string StripPrefix(string id)
    {
        var index = id.IndexOf(':');
        return index >= 0 ? id[(index + 1)..] : id;
    }

    private bool IsVisible(TopologyNode node) =>
        _showLowLevel || !LowLevelTypes.Contains(node.Type);

    private static int TypeOrder(string kind) => kind switch
    {
        "application" => 0,
        "datasource" => 1,
        "mq" => 2,
        "service" => 3,
        "container" => 4,
        "deploy" => 5,
        "domain" => 6,
        "port" => 7,
        "cert" => 8,
        _ => 9
    };

    private static string BuildLaneSummary(IReadOnlyCollection<AssetRow> assets)
    {
        if (assets.Count == 0)
            return "无托管资产";

        var apps = assets.Count(a => a.Kind == "application");
        var data = assets.Count(a => a.Kind == "datasource" || a.Kind == "mq");
        var other = assets.Count - apps - data;
        var parts = new List<string>();
        if (apps > 0) parts.Add($"{apps} 应用");
        if (data > 0) parts.Add($"{data} 数据");
        if (other > 0) parts.Add($"{other} 其他");
        return string.Join(" / ", parts);
    }

    private AssetRow ToAsset(TopologyNode node)
    {
        var host = InfoString(node, "host");
        var port = InfoInt(node, "port");
        var accessProfiles = InfoInt(node, "accessProfileCount") ?? 1;
        var subtitle = new List<string>();
        if (!string.IsNullOrWhiteSpace(host))
            subtitle.Add(port is > 0 ? $"{host}:{port}" : host);
        if (accessProfiles > 1)
            subtitle.Add($"{accessProfiles} 接入");
        if (node.Id.Contains(":disc:", StringComparison.Ordinal))
            subtitle.Add("待确认");

        var detail = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(host))
            detail.AppendLine($"主机: {host}{(port is > 0 ? ":" + port : string.Empty)}");
        var aliases = InfoStrings(node, "aliases");
        if (aliases.Length > 0)
            detail.AppendLine("别名: " + string.Join(", ", aliases));
        var kinds = InfoStrings(node, "identityKinds");
        if (kinds.Length > 0)
            detail.AppendLine("身份: " + string.Join(", ", kinds));
        var username = InfoString(node, "username");
        if (!string.IsNullOrWhiteSpace(username))
            detail.AppendLine("账号: " + username);
        var accessMode = InfoString(node, "accessMode");
        if (!string.IsNullOrWhiteSpace(accessMode))
            detail.AppendLine("访问模式: " + accessMode);

        return new AssetRow
        {
            Id = node.Id,
            Label = string.IsNullOrWhiteSpace(node.Label) ? node.Id : node.Label,
            Kind = node.Type,
            KindLabel = KindLabel(node.Type),
            Subtitle = string.Join(" · ", subtitle),
            Detail = detail.ToString().TrimEnd()
        };
    }

    private bool IsCrossServer(string fromId, string toId)
    {
        var fromHost = HostOf(fromId);
        var toHost = HostOf(toId);
        return fromHost is not null && toHost is not null &&
               !string.Equals(fromHost, toHost, StringComparison.OrdinalIgnoreCase);
    }

    private string Label(string id) => _nodeById.GetValueOrDefault(id)?.Label ?? id;

    private string? HostOf(string nodeId)
    {
        if (_graph is null || !_nodeById.TryGetValue(nodeId, out var node))
            return null;

        if (node.Type == "ssh")
            return node.Id;

        var runsOn = _graph.Edges.FirstOrDefault(e => e.From == nodeId && e.Type == "runsOn");
        if (runsOn is not null && _nodeById.TryGetValue(runsOn.To, out var host) && host.Type == "ssh")
            return host.Id;

        var resolvesTo = _graph.Edges.FirstOrDefault(e => e.From == nodeId && e.Type == "resolvesTo");
        if (resolvesTo is not null && _nodeById.TryGetValue(resolvesTo.To, out var resolved) && resolved.Type == "ssh")
            return resolved.Id;

        var hostedOn = _graph.Edges.FirstOrDefault(e => e.From == nodeId && e.Type == "hostedOn");
        if (hostedOn is not null && _nodeById.TryGetValue(hostedOn.To, out var portHost) && portHost.Type == "ssh")
            return portHost.Id;

        return null;
    }

    private static string KindLabel(string type) => type switch
    {
        "ssh" => "SSH服务器",
        "application" => "应用",
        "datasource" => "数据库",
        "service" => "服务",
        "container" => "容器",
        "deploy" => "部署",
        "domain" => "域名",
        "port" => "端口",
        "cert" => "证书",
        "mq" => "消息队列",
        "system" => "系统",
        _ => type
    };

    private static string InfoString(TopologyNode node, string key)
    {
        if (!node.Info.TryGetValue(key, out var value))
            return string.Empty;
        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            _ => string.Empty
        };
    }

    private static int? InfoInt(TopologyNode node, string key)
    {
        if (!node.Info.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            int number => number,
            long number => (int)number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed) => parsed,
            _ => null
        };
    }

    private static string[] InfoStrings(TopologyNode node, string key)
    {
        if (!node.Info.TryGetValue(key, out var value) || value is null)
            return Array.Empty<string>();

        return value switch
        {
            string[] values => values,
            IEnumerable<string> values => values.ToArray(),
            JsonElement { ValueKind: JsonValueKind.Array } element => element
                .EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Where(x => x.Length > 0)
                .ToArray(),
            _ => Array.Empty<string>()
        };
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AssetRow
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string KindLabel { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public bool IsHighlighted { get; set; }
}

public sealed class ServerLaneRow
{
    public ServerLaneRow(AssetRow server, IReadOnlyList<AssetRow> children, string childSummary)
    {
        Server = server;
        Children = children;
        ChildSummary = childSummary;
    }

    public AssetRow Server { get; }
    public IReadOnlyList<AssetRow> Children { get; }
    public string ChildSummary { get; }
}

public sealed class DependencyRow
{
    public string FromId { get; set; } = string.Empty;
    public string ToId { get; set; } = string.Empty;
    public string FromLabel { get; set; } = string.Empty;
    public string ToLabel { get; set; } = string.Empty;
    public string Relation { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public bool IsCrossServer { get; set; }
}

public sealed class FlowGroupRow
{
    public FlowGroupRow(string title, IReadOnlyList<AssetRow> nodes)
    {
        Title = title;
        Nodes = nodes;
    }

    public string Title { get; }
    public IReadOnlyList<AssetRow> Nodes { get; }
}

public sealed class FlowPathRow
{
    public FlowPathRow(string direction, string text, int hops)
    {
        Direction = direction;
        Text = text;
        Hops = hops;
    }

    public string Direction { get; }
    public string Text { get; }
    public int Hops { get; }
}

public sealed class MatrixRow
{
    public MatrixRow(string serverLabel, IReadOnlyList<MatrixCell> cells)
    {
        ServerLabel = serverLabel;
        Cells = cells;
    }

    public string ServerLabel { get; }
    public IReadOnlyList<MatrixCell> Cells { get; }
}

public sealed class MatrixCell
{
    public string Text { get; set; } = "·";
    public bool HasValue { get; set; }
}

public sealed class SuggestionRow
{
    public SuggestionRow(AssetConfigurationSuggestion suggestion)
    {
        Suggestion = suggestion;
    }

    public AssetConfigurationSuggestion Suggestion { get; }
    public string Title => $"{Suggestion.AssetName} · {KindLabel(Suggestion.Kind)}";
    public string AssetType => Suggestion.AssetType;
    public string Value => Suggestion.Kind == AssetSuggestionKind.Url
        ? $"{Suggestion.Value}  ({Suggestion.Role})"
        : Suggestion.Value;
    public string Reason => Suggestion.Reason;
    public string ConfidenceText => $"{Suggestion.Confidence:P0}";

    private static string KindLabel(AssetSuggestionKind kind) => kind switch
    {
        AssetSuggestionKind.AssetKey => "补 AssetKey",
        AssetSuggestionKind.RuntimeSsh => "运行环境 → SSH",
        AssetSuggestionKind.RuntimeDocker => "运行环境 → Docker",
        AssetSuggestionKind.DockerContainer => "补容器/Compose service",
        AssetSuggestionKind.Url => "补 URL",
        _ => kind.ToString()
    };
}

public sealed class IssueRow
{
    public IssueRow(AssetConfigurationIssue issue)
    {
        Issue = issue;
    }

    public AssetConfigurationIssue Issue { get; }
    public string Title => $"{Issue.AssetName} ({Issue.AssetType})";
    public string Code => Issue.Code;
    public string Message => Issue.Message;
    public string Severity => Issue.Severity switch
    {
        AssetConfigurationIssueSeverity.Error => "错误",
        AssetConfigurationIssueSeverity.Warning => "警告",
        _ => "提示"
    };
}
