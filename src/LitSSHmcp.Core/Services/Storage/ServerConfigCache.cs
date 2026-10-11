using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// 最近一次加载的服务器配置**只读快照**（按 Id 索引）。供需要在同步路径解析"跳板机(ProxyJump)"的
/// <c>SshClientFactory</c> 使用——连接工厂是同步的、拿不到 <see cref="IConfigService"/>。
/// 由 <see cref="ConfigService"/> 在每次 Load/Save 后刷新；连接发生前通常已经加载过配置，故命中率足够。
/// </summary>
public static class ServerConfigCache
{
    private static IReadOnlyDictionary<string, SshServerConfig> _byId =
        new Dictionary<string, SshServerConfig>(StringComparer.OrdinalIgnoreCase);

    public static void Update(IEnumerable<SshServerConfig>? servers)
    {
        var map = new Dictionary<string, SshServerConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers ?? Enumerable.Empty<SshServerConfig>())
            if (!string.IsNullOrEmpty(server.Id))
                map[server.Id] = server;
        _byId = map;
    }

    public static SshServerConfig? Find(string id) =>
        !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id, out var server) ? server : null;

    /// <summary>由跳板服务器 Id 生成 <c>user@host:port</c> 端点（供 rsync/ssh <c>-J</c> 用）；无效返回 null。</summary>
    public static string? JumpEndpoint(string? jumpServerId)
    {
        if (string.IsNullOrWhiteSpace(jumpServerId))
            return null;
        var jump = Find(jumpServerId!);
        return jump is null || string.IsNullOrWhiteSpace(jump.Host)
            ? null
            : $"{jump.Username}@{jump.Host}:{jump.Port}";
    }
}
