using System.Text;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Topology;

/// <summary>
/// 把资产清单（服务器/数据源/应用/关系）渲染为注入系统提示的紧凑文本快照。
/// 纯函数、无 IO、无时间戳（保证会话内 prompt 前缀稳定，不打散缓存）；
/// 超过 <see cref="MaxChars"/> 时按「应用细节 → 关系数 → 条目裁剪」顺序降级。
/// </summary>
public static class AssetGraphPromptFormatter
{
    public const int MaxChars = 4000;

    /// <summary>渲染资产图谱文本；无任何启用资产时返回 null。</summary>
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

        var text = Render(s, d, a, r, includeAppDetails: true);
        if (text.Length <= MaxChars)
            return text;

        text = Render(s, d, a, r, includeAppDetails: false);
        if (text.Length <= MaxChars)
            return text;

        // 仍超长：先砍关系，再逐段裁剪条目（保底保服务器/数据源头部条目）
        text = Render(s, d, a, [], includeAppDetails: false);
        if (text.Length <= MaxChars)
            return text;

        return Truncate(text, MaxChars);
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
        bool includeAppDetails)
    {
        var sb = new StringBuilder();
        if (servers.Count > 0)
        {
            sb.AppendLine("[服务器]");
            foreach (var x in servers)
                sb.AppendLine($"- {AssetNode.Ssh(x.Id)} | {x.Name} | {x.Host}:{x.Port}"
                    + (x.Tags.Length > 0 ? $" | tags={string.Join(",", x.Tags)}" : "")
                    + (string.IsNullOrWhiteSpace(x.Description) ? "" : $" | {x.Description}"));
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
                if (!string.IsNullOrWhiteSpace(x.ContainerName)) line.Append($" container={x.ContainerName}");
                if (includeAppDetails)
                {
                    if (!string.IsNullOrWhiteSpace(x.Path)) line.Append($" path={x.Path}");
                    if (x.LogPaths.Length > 0) line.Append($" logs={string.Join(";", x.LogPaths)}");
                }
                sb.AppendLine(line.ToString());
            }
        }
        if (relations.Count > 0)
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
        return text[..cut].TrimEnd() + "\n…(资产过多, 以上为节选; 完整清单用 ssh_list_servers/datasource_list 等列表工具获取)";
    }
}
