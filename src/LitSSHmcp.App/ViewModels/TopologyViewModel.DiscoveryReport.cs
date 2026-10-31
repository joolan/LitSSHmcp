using System.Text;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class TopologyViewModel
{
    private string _discoveryReport = "尚未运行自动发现。\r\n点击「自动发现」后，这里会显示：扫描的服务器、说明(notes)、疑似重复的接入配置、未匹配端点(含来源文件/URL/跳过原因)、错误。";

    /// <summary>最近一次自动发现的详细报告（说明/疑似重复/未匹配端点/错误），供「发现报告」窗口展示。</summary>
    public string DiscoveryReport
    {
        get => _discoveryReport;
        private set { _discoveryReport = value; OnPropertyChanged(); }
    }

    private static string BuildDiscoveryReport(DiscoveryResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"自动发现报告  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"新增边 {r.NewEdges.Length} / 更新边 {r.UpdatedEdges.Length} / 错误 {r.Errors.Length}");
        if (r.IdentityCount > 0)
            sb.AppendLine($"身份采集: {r.IdentityCount} 条" +
                          (r.IdentityConflicts.Count > 0 ? $"，冲突 {r.IdentityConflicts.Count} 条" : string.Empty));
        if (r.ScannedServers.Length > 0)
            sb.AppendLine($"扫描服务器: {string.Join(", ", r.ScannedServers)}");
        sb.AppendLine();

        AppendReportSection(sb, "错误", r.Errors);
        AppendReportSection(sb, "说明 (notes)", r.Notes);
        AppendReportSection(sb, "身份冲突", r.IdentityConflicts.ToArray());

        if (r.DuplicateCandidates.Count > 0)
        {
            sb.AppendLine($"疑似重复的接入配置 ({r.DuplicateCandidates.Count} 组):");
            foreach (var group in r.DuplicateCandidates)
            {
                sb.AppendLine($"  - [{group.AssetType}] {group.MatchKey}");
                sb.AppendLine($"      原因: {group.Reason}");
                foreach (var item in group.Items)
                {
                    sb.AppendLine($"      · {item.Name} ({item.Id})  {item.Host}:{item.Port}");
                    if (!string.IsNullOrWhiteSpace(item.Detail))
                        sb.AppendLine($"        {item.Detail}");
                }
            }
            sb.AppendLine("  提示: 如果这些是同一实例的不同接入方式（公网/内网/跳板/不同账号），可以保留；后续按资产身份归并。");
            sb.AppendLine();
        }

        if (r.UnmatchedEndpoints.Count > 0)
        {
            sb.AppendLine($"未匹配端点 / 已跳过端点 ({r.UnmatchedEndpoints.Count}):");
            foreach (var d in r.UnmatchedEndpoints)
            {
                var server = d.GetValueOrDefault("server");
                var url = d.GetValueOrDefault("url");
                var file = d.GetValueOrDefault("file");
                var note = d.GetValueOrDefault("note");
                sb.AppendLine($"  - [{server}] {url}");
                if (file != null) sb.AppendLine($"      文件: {file}");
                if (note != null) sb.AppendLine($"      原因: {note}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendReportSection(StringBuilder sb, string title, string[] items)
    {
        if (items.Length == 0)
            return;
        sb.AppendLine($"{title} ({items.Length}):");
        foreach (var item in items)
            sb.AppendLine("  - " + item);
        sb.AppendLine();
    }
}
