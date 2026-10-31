using System.Text;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class TopologyViewModel
{
    private string _selectedEvidence = string.Empty;
    private bool _showDiscoveryEvidence;

    public string SelectedEvidence
    {
        get => _selectedEvidence;
        private set { _selectedEvidence = value; OnPropertyChanged(nameof(SelectedEvidence)); }
    }

    public bool ShowDiscoveryEvidence
    {
        get => _showDiscoveryEvidence;
        set
        {
            if (_showDiscoveryEvidence == value)
                return;
            _showDiscoveryEvidence = value;
            OnPropertyChanged(nameof(ShowDiscoveryEvidence));
            _ = RefreshEvidenceAsync(_selectedId);
        }
    }

    private async Task RefreshEvidenceAsync(string? nodeId)
    {
        SelectedEvidence = string.Empty;
        if (!ShowDiscoveryEvidence || string.IsNullOrWhiteSpace(nodeId) || _graph is null)
            return;

        var node = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null || node.Type != "ssh")
            return;

        var serverId = NodeProfileIds(node).FirstOrDefault() ?? StripNodePrefix(nodeId);
        try
        {
            var evidence = await _topology.GetServerDiscoveryEvidenceAsync(serverId);
            if (_selectedId != nodeId)
                return;
            SelectedEvidence = FormatEvidence(evidence);
        }
        catch (Exception ex)
        {
            if (_selectedId == nodeId)
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
                var managed = port.SystemdManaged ? $"systemd: {port.Unit}" : "手工/未识别管理方式";
                sb.AppendLine($"  {port.Protocol}/{port.Port}  {port.Process}  [{managed}]" +
                              (string.IsNullOrWhiteSpace(port.Executable) ? string.Empty : $"  {port.Executable}"));
            }
        }

        if (evidence.Services.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("系统服务:");
            foreach (var service in evidence.Services.Take(20))
                sb.AppendLine($"  {service.Unit}  {service.Active}/{service.Sub}");
        }

        if (evidence.Containers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("容器:");
            foreach (var container in evidence.Containers.Take(20))
                sb.AppendLine($"  {container.Name}  {container.Image}  {container.State}");
        }

        if (evidence.Domains.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Nginx 站点 / 证书:");
            foreach (var domain in evidence.Domains.Take(20))
                sb.AppendLine($"  {domain.Domain}  ssl={domain.Ssl}  ports={string.Join(",", domain.Ports)}");
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

    private static string StripNodePrefix(string id)
    {
        var index = id.IndexOf(':');
        return index >= 0 ? id[(index + 1)..] : id;
    }
}
