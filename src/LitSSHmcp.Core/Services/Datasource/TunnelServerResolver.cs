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
        "请在配置中设?tunnelServerId，或添加 'ssh:<服务器ID> -> ds:<数据源ID>' ?canAccess 关系。";

    /// <summary>跳板 SSH 服务器被禁用时的拒绝文案（不放行连接）。</summary>
    public static string DisabledMessage(SshServerConfig server) =>
        $"跳板 SSH 服务器 {server.Name} ({server.Username}@{server.Host}:{server.Port}) 已被禁用, 已拒绝建立隧道。" +
        "请在桌面 App 的「服务器编辑」里取消\"禁用\"并保存, 或改用其它跳板。";
}
