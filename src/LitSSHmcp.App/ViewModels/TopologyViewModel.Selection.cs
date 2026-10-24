namespace LitSSHmcp.App.ViewModels;

public partial class TopologyViewModel
{
    public GraphItemVm? SelectedItemOrNull => _selectedId == null ? null : FindItem(_selectedId);

    public string SelectedNodeName => SelectedItemOrNull switch
    {
        GraphNodeVm node when !string.IsNullOrWhiteSpace(node.Label) => node.Label,
        GraphChipVm chip when !string.IsNullOrWhiteSpace(chip.Label) => chip.Label,
        GraphBoxVm box when !string.IsNullOrWhiteSpace(box.Label) => box.Label,
        { } item => item.Id,
        _ => string.Empty
    };

    public string SelectedNodeType => SelectedItemOrNull switch
    {
        GraphNodeVm node => node.Type,
        GraphChipVm chip => chip.Type,
        GraphBoxVm => "server",
        _ => string.Empty
    };

    public string SelectedNodeTypeText => SelectedNodeType.ToLowerInvariant() switch
    {
        "server" => "SSH 服务器",
        "app" => "应用",
        "db" or "database" => "数据库",
        "service" => "服务",
        "container" => "容器",
        "deploy" => "部署",
        "domain" => "域名",
        "port" => "端口",
        "cert" => "证书",
        "system" => "系统",
        "mq" => "消息队列",
        _ => string.IsNullOrWhiteSpace(SelectedNodeType) ? "资产" : SelectedNodeType
    };

    public string SelectedNodeDetail => SelectedItemOrNull?.Tooltip ?? string.Empty;
    public string SelectedNodeId => _selectedId ?? string.Empty;
    public bool HasDiscoveredSelection => _selectedId?.Contains(":disc:", StringComparison.Ordinal) == true;
    public bool IsEmptyGraph => _allItems.Length == 0;

    public void SelectNode(string? id)
    {
        _selectedId = id;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedItemOrNull));
        OnPropertyChanged(nameof(SelectedNodeName));
        OnPropertyChanged(nameof(SelectedNodeType));
        OnPropertyChanged(nameof(SelectedNodeTypeText));
        OnPropertyChanged(nameof(SelectedNodeDetail));
        OnPropertyChanged(nameof(SelectedNodeId));
        OnPropertyChanged(nameof(HasDiscoveredSelection));
        UpdateSelectionRect();
        ApplyImpactHighlight(id);
    }
}
