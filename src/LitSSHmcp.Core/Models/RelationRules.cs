namespace LitSSHmcp.Core.Models;

/// <summary>
/// 拓扑关系的逻辑校验：保证关系类型与起止节点的类型匹配。
/// - runsOn    : 应用/数据库 运行在 服务器   (from = app|ds, to = ssh)
/// - connectsTo: 应用 连接 数据库            (from = app,    to = ds)
/// - canAccess : 服务器 可访问 数据库        (from = ssh,    to = ds)
/// - relatedTo : 通用关系（仅禁止自环）
/// </summary>
public static class RelationRules
{
    public static readonly string[] KnownTypes = { "runsOn", "connectsTo", "canAccess", "relatedTo" };

    public static bool TryValidate(string? from, string? to, string? type, out string? error)
    {
        error = null;

        var f = PrefixOf(from);
        var t = PrefixOf(to);
        if (f is null || t is null)
        {
            error = "节点ID需以 ssh: / ds: / app: 开头（可从下拉选择）";
            return false;
        }

        if (string.Equals(from?.Trim(), to?.Trim(), StringComparison.Ordinal))
        {
            error = "关系的起点与终点不能是同一节点";
            return false;
        }

        switch ((type ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "runson":
                if (f != "app" && f != "ds")
                {
                    error = "runsOn 只能是“应用/数据库 运行在 服务器”（from=app: 或 ds:）";
                    return false;
                }
                if (t != "ssh")
                {
                    error = "runsOn 的终点必须是服务器（to=ssh:...）";
                    return false;
                }
                return true;

            case "connectsto":
                if (f != "app" || t != "ds")
                {
                    error = "connectsTo 只能是“应用 连接 数据库”（from=app:，to=ds:）";
                    return false;
                }
                return true;

            case "canaccess":
                if (f != "ssh" || t != "ds")
                {
                    error = "canAccess 只能是“服务器 可访问 数据库”（from=ssh:，to=ds:）";
                    return false;
                }
                return true;

            case "relatedto":
                return true;

            default:
                error = $"未知关系类型 '{type}'（可用: {string.Join(" / ", KnownTypes)}）";
                return false;
        }
    }

    /// <summary>返回节点前缀（ssh/ds/app），非已知格式返回 null。</summary>
    public static string? PrefixOf(string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return null;

        var value = nodeId.Trim();
        if (value.StartsWith(AssetNode.SshPrefix, StringComparison.Ordinal)) return "ssh";
        if (value.StartsWith(AssetNode.DsPrefix, StringComparison.Ordinal)) return "ds";
        if (value.StartsWith(AssetNode.AppPrefix, StringComparison.Ordinal)) return "app";
        return null;
    }
}
