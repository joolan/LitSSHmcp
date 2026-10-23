using System.Text;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Topology;

/// <summary>
/// 把人工配置的资产（服务器/数据源/应用/关系）渲染为注入系统提示的稳定摘要。
/// 纯函数、无 IO、无时间戳/发现细节（保证会话内 prompt 前缀稳定，不打散缓存）；
/// 超过 <see cref="MaxChars"/> 时先丢弃关系，再按行截断，并提示改用 topology_* 工具查询完整细节。
/// </summary>
public static class AssetGraphPromptFormatter
{
    /// <summary>稳定摘要上限；发现细节（服务/容器/端口/域名/证书/证据）不进系统提示。</summary>
    public const int MaxChars = 2000;

    /// <summary>渲染资产摘要；无任何启用资产时返回 null。</summary>
    public static string? Format(
        IReadOnlyList<SshServerConfig>? servers,
        IReadOnlyList<DataSourceConfig>? dataSources,
        IReadOnlyList<ApplicationConfig>? applications,
        IReadOnlyList<RelationConfig>? relations)
    {
        var s = (servers ?? Array.Empty<SshServerConfig>()).Where(x => x is { Disabled: false }).ToList();
        var d = (dataSources ?? Array.Empty<DataSourceConfig>()).Where(x => x is not null).ToList();
        var a = (applications ?? Array.Empty<ApplicationConfig>()).Where(x => x is not null).ToList();
        var r = (relations ?? Array.Empty<RelationConfig>())
            .Where(x => !string.IsNullOrWhiteSpace(x.From) && !string.IsNullOrWhiteSpace(x.To)).ToList();
        if (s.Count == 0 && d.Count == 0 && a.Count == 0)
            return null;

        var text = Render(s, d, a, r, includeRelations: true);
        if (text.Length <= MaxChars)
            return text;

        // 关系通常最占空间，且可由 topology_query action=neighbors/path 按需查询：先丢弃关系保留全部资产 ID。
        text = Render(s, d, a, [], includeRelations: false);
        return text.Length <= MaxChars ? text : Truncate(text, MaxChars);
    }

    /// <summary>便捷入口：从 AppConfig 渲染（Disabled 服务器已排除）。</summary>
    public static string? Format(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Format(config.Servers, config.DataSources, config.Applications, config.Relations);
    }

    private static string Render(
        List<SshServerConfig> servers,
        List<DataSourceConfig> dataSources,
        List<ApplicationConfig> applications,
        List<RelationConfig> relations,
        bool includeRelations)
    {
        var sb = new StringBuilder();
        if (servers.Count > 0)
        {
            sb.AppendLine("[服务器]");
            foreach (var x in servers)
                sb.AppendLine($"- {AssetNode.Ssh(x.Id)} | {x.Name} | {x.Host}:{x.Port}"
                    + (x.Tags.Length > 0 ? $" | tags={string.Join(",", x.Tags)}" : ""));
        }
        if (dataSources.Count > 0)
        {
            sb.AppendLine("[数据源]");
            foreach (var x in dataSources)
                sb.AppendLine($"- {AssetNode.Ds(x.Id)} | {x.Type} {x.Name} | {x.Host}:{x.Port}"
                    + (string.IsNullOrWhiteSpace(x.DefaultDatabase) ? "" : $" db={x.DefaultDatabase}")
                    + (x.AccessMode == AccessMode.SshTunnel && !string.IsNullOrWhiteSpace(x.TunnelServerId)
                        ? $" | 经隧道 ssh:{x.TunnelServerId}" : ""));
        }
        if (applications.Count > 0)
        {
            sb.AppendLine("[应用]");
            foreach (var x in applications)
            {
                var line = new StringBuilder($"- {AssetNode.App(x.Id)} | {x.Type} {x.Name}");
                if (x.Port is int port) line.Append($" :{port}");
                if (!string.IsNullOrWhiteSpace(x.Host)) line.Append($" host={x.Host}");
                sb.AppendLine(line.ToString());
            }
        }
        if (includeRelations && relations.Count > 0)
        {
            sb.AppendLine("[关系]");
            foreach (var x in relations)
                sb.AppendLine($"- {x.From} -{x.Type}-> {x.To}"
                    + (string.IsNullOrWhiteSpace(x.Note) ? "" : $" ({x.Note})"));
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        // 截断到最后一行边界，避免留下半行误导模型
        var cut = text.LastIndexOf('\n', maxChars - 1);
        if (cut < maxChars / 2) cut = maxChars - 1;
        return text[..cut].TrimEnd()
            + "\n…(资产摘要已截断；完整清单用 ssh_list_servers/datasource_list，关系用 topology_query action=search/neighbors 查询)";
    }
}
