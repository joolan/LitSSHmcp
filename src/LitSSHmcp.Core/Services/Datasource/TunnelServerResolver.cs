using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Datasource;

/// <summary>数据源"经 SSH 隧道"模式下跳板服务器的解析（优先 tunnelServerId，其次 ssh->ds 的 canAccess 关系）。</summary>
public static class TunnelServerResolver
{
    public static SshServerConfig? Resolve(AppConfig config, DataSourceConfig ds)
    {
        if (!string.IsNullOrEmpty(ds.TunnelServerId))
            return config.Servers.FirstOrDefault(s => s.Id == ds.TunnelServerId);

        var edge = config.Relations.FirstOrDefault(r =>
            r.To == AssetNode.Ds(ds.Id) &&
            r.From.StartsWith(AssetNode.SshPrefix, StringComparison.Ordinal));

        if (edge == null) return null;
        var serverId = edge.From[AssetNode.SshPrefix.Length..];
        return config.Servers.FirstOrDefault(s => s.Id == serverId);
    }

    public const string MissingServerMessage =
        "数据源配置为 SSH 隧道模式，但未找到可用的跳板服务器。" +
        "请在配置中设置 tunnelServerId，或添加 'ssh:<服务器ID> -> ds:<数据源ID>' 的 canAccess 关系。";
}
