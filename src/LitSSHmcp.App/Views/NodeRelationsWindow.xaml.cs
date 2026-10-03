using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.Views;

public class RelationRow
{
    public string From { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool IsManual { get; set; }
    public string FromLabel { get; set; } = string.Empty;
    public string ToLabel { get; set; } = string.Empty;
    public bool FromIsCurrent { get; set; }
    public bool ToIsCurrent { get; set; }
}

public partial class NodeRelationsWindow : Window
{
    private readonly string _nodeId;
    private readonly IConfigService _config = AppServiceFactory.CreateConfigService();
    private readonly ITopologyService _topology;
    private readonly ITopologyStore _store = new TopologyStore();
    private readonly ObservableCollection<RelationRow> _rows = new();
    private RelationRow? _selected;

    public NodeRelationsWindow(string nodeId)
    {
        InitializeComponent();
        _nodeId = nodeId;
        _topology = AppServiceFactory.CreateTopologyService(_config);
        HeaderText.Text = $"节点 {nodeId} 的关系";
        RelationList.ItemsSource = _rows;
        foreach (var type in new[] { "runsOn", "connectsTo", "canAccess", "relatedTo" })
            TypeBox.Items.Add(type);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _rows.Clear();
        _selected = null;

        TopologyGraph? graph = null;
        try
        {
            graph = await _topology.GetGraphAsync();
        }
        catch
        {
            // 图读取失败时退化为只显示手动关系
        }

        var labelById = graph?.Nodes.ToDictionary(n => n.Id, LabelWithPort) ?? new Dictionary<string, string>();
        string LabelOf(string id) => labelById.TryGetValue(id, out var label) && !string.IsNullOrWhiteSpace(label) ? label : id;

        var config = await _config.LoadConfigAsync();
        foreach (var r in config.Relations.Where(r => r.From == _nodeId || r.To == _nodeId))
        {
            _rows.Add(new RelationRow
            {
                From = r.From,
                Type = r.Type,
                To = r.To,
                Note = r.Note,
                Source = "手动",
                IsManual = true,
                FromLabel = LabelOf(r.From),
                ToLabel = LabelOf(r.To),
                FromIsCurrent = r.From == _nodeId,
                ToIsCurrent = r.To == _nodeId
            });
        }

        if (graph != null)
        {
            foreach (var e in graph.Edges.Where(e =>
                         string.Equals(e.Source, "discovered", StringComparison.OrdinalIgnoreCase) &&
                         (e.From == _nodeId || e.To == _nodeId)))
            {
                if (_rows.Any(r => r.From == e.From && r.To == e.To && r.Type == e.Type))
                    continue;

                _rows.Add(new RelationRow
                {
                    From = e.From,
                    Type = e.Type,
                    To = e.To,
                    Source = "自动发现",
                    IsManual = false,
                    FromLabel = LabelOf(e.From),
                    ToLabel = LabelOf(e.To),
                    FromIsCurrent = e.From == _nodeId,
                    ToIsCurrent = e.To == _nodeId
                });
            }
        }

        HeaderText.Text = _rows.Count == 0
            ? $"节点 {LabelOf(_nodeId)} 暂无关系"
            : $"节点 {LabelOf(_nodeId)} 的关系（共 {_rows.Count} 条）";
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

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = RelationList.SelectedItem as RelationRow;
        var editable = _selected is { IsManual: true };
        TypeBox.IsEnabled = editable;
        NoteBox.IsEnabled = editable;
        TypeBox.SelectedItem = _selected?.Type;
        NoteBox.Text = _selected?.Note ?? string.Empty;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsManual: true } row)
        {
            MessageBox.Show(this, "自动发现的关系不可编辑，只能删除。", "节点关系", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newType = TypeBox.SelectedItem as string ?? row.Type;
        try
        {
            var config = await _config.LoadConfigAsync();
            var disabled = DisabledServerOf(config, row.From) ?? DisabledServerOf(config, row.To);
            if (disabled != null)
            {
                MessageBox.Show(this, $"该关系的一端是已禁用的服务器 {disabled.Name}, 已拒绝修改关系。" +
                    "请先在服务器编辑里取消\"禁用\"。", "节点关系", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var index = Array.FindIndex(config.Relations, r => r.From == row.From && r.To == row.To && r.Type == row.Type);
            if (index < 0)
            {
                MessageBox.Show(this, "原关系不存在，请刷新。", "节点关系", MessageBoxButton.OK, MessageBoxImage.Warning);
                await LoadAsync();
                return;
            }

            var others = config.Relations.Where((_, i) => i != index).ToArray();
            if (!RelationRules.TryValidate(row.From, row.To, newType, others, out var error))
            {
                MessageBox.Show(this, $"不能保存：{error}", "节点关系", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            config.Relations[index] = new RelationConfig
            {
                From = row.From,
                To = row.To,
                Type = newType,
                Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim()
            };
            await _config.SaveConfigAsync(config);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "节点关系", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } row)
            return;

        if (MessageBox.Show(this, $"删除关系 {row.From} --{row.Type}--> {row.To} ?", "节点关系",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            if (row.IsManual)
            {
                var config = await _config.LoadConfigAsync();
                config.Relations = config.Relations
                    .Where(r => !(r.From == row.From && r.To == row.To && r.Type == row.Type))
                    .ToArray();
                await _config.SaveConfigAsync(config);
            }
            else
            {
                await _store.InitializeAsync();
                await _store.RemoveEdgeAsync(row.From, row.To, row.Type);
            }

            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"删除失败：{ex.Message}", "节点关系", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>节点 id 指向的服务器若处于禁用状态则返回该服务器（禁用的服务器不允许在可视化窗口里建链/改链）。</summary>
    private static SshServerConfig? DisabledServerOf(AppConfig config, string nodeId) =>
        nodeId.StartsWith("ssh:", StringComparison.Ordinal)
            ? config.Servers.FirstOrDefault(s => s.Id == nodeId[4..] && s.Disabled)
            : null;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
