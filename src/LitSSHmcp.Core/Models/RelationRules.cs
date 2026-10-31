namespace LitSSHmcp.Core.Models;

/// <summary>
/// 拓扑关系的逻辑校验：保证关系类型与起止节点的类型匹配。
/// - runsOn      : 应用/数据库/服务/容器 运行在 服务器 (from = app|ds|service|container, to = ssh)
/// - runsIn      : 应用/数据库 运行在 Docker 应用        (from = app|ds, to = app)
/// - connectsTo  : 应用/服务/容器 连接 数据库/应用/服务    (from = app|service|container, to = ds|app|service|mq)
/// - canAccess   : 服务器 可访问 数据库                     (from = ssh, to = ds)
/// - hostedOn    : 端口 监听在 服务器                       (from = port, to = ssh)
/// - listensOn   : 应用/数据库/服务/容器 监听 端口           (from = app|ds|service|container, to = port)
/// - exposes     : 域名 暴露 应用/服务                      (from = domain, to = app|service)
/// - tlsBy       : 域名 使用 证书                           (from = domain, to = cert)
/// - resolvesTo  : 域名 解析到 服务器                       (from = domain, to = ssh)
/// - upstreamOf  : 应用/服务/容器 是 应用/服务/容器 的上游    (from/to = app|service|container)
/// - deployedFrom: 应用/服务/容器 由 部署记录 部署           (from = app|service|container, to = deploy)
/// - partOf      : 应用/服务/容器 属于 应用/服务/系统        (from = app|service|container, to = app|service|system)
/// - accessedVia : 服务器/数据源 通过 服务器 访问                (from = ssh|ds, to = ssh)
/// - relatedTo   : 通用关系（仅禁止自环）
/// </summary>
public static class RelationRules
{
    public static readonly string[] KnownTypes =
    {
        "runsOn", "runsIn", "connectsTo", "canAccess", "hostedOn", "listensOn", "exposes", "tlsBy",
        "resolvesTo", "upstreamOf", "deployedFrom", "partOf", "relatedTo", "accessedVia"
    };

    public static bool TryValidate(string? from, string? to, string? type, out string? error)
    {
        error = null;

        var f = PrefixOf(from);
        var t = PrefixOf(to);
        if (f is null || t is null)
        {
            error = "节点ID需以 ssh: / ds: / app: / service: / container: / deploy: / system: / mq: / domain: / port: / cert: 开头（可从下拉选择）";
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
                if (f != "app" && f != "ds" && f != "service" && f != "container")
                {
                    error = "runsOn 只能是“应用/数据库/服务/容器 运行在 服务器”（from=app:/ds:/service:/container:）";
                    return false;
                }
                if (t != "ssh")
                {
                    error = "runsOn 的终点必须是服务器（to=ssh:...）";
                    return false;
                }
                return true;

            case "runsin":
                if (f != "app" && f != "ds")
                {
                    error = "runsIn 的起点必须是应用或数据源（from=app:/ds:）";
                    return false;
                }
                if (t != "app")
                {
                    error = "runsIn 的终点必须是 Docker 应用（to=app:...）";
                    return false;
                }
                return true;

            case "connectsto":
                if (f != "app" && f != "service" && f != "container")
                {
                    error = "connectsTo 的起点必须是应用/服务/容器（from=app:/service:/container:）";
                    return false;
                }
                if (t != "ds" && t != "app" && t != "service" && t != "mq")
                {
                    error = "connectsTo 的终点必须是数据库/应用/服务/消息队列（to=ds:/app:/service:/mq:）";
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

            case "hostedon":
                if (f != "port" || t != "ssh")
                {
                    error = "hostedOn 只能是“端口 监听在 服务器”（from=port:，to=ssh:）";
                    return false;
                }
                return true;

            case "listenson":
                if (f != "app" && f != "ds" && f != "service" && f != "container")
                {
                    error = "listensOn 只能是“应用/数据库/服务/容器 监听 端口”（from=app:/ds:/service:/container:）";
                    return false;
                }
                if (t != "port")
                {
                    error = "listensOn 的终点必须是端口（to=port:...）";
                    return false;
                }
                return true;

            case "exposes":
                if (f != "domain" || (t != "app" && t != "service"))
                {
                    error = "exposes 只能是“域名 暴露 应用/服务”（from=domain:，to=app:/service:）";
                    return false;
                }
                return true;

            case "tlsby":
                if (f != "domain" || t != "cert")
                {
                    error = "tlsBy 只能是“域名 使用 证书”（from=domain:，to=cert:）";
                    return false;
                }
                return true;

            case "resolvesto":
                if (f != "domain" || t != "ssh")
                {
                    error = "resolvesTo 只能是“域名 解析到 服务器”（from=domain:，to=ssh:）";
                    return false;
                }
                return true;

            case "upstreamof":
                if ((f != "app" && f != "service" && f != "container") ||
                    (t != "app" && t != "service" && t != "container"))
                {
                    error = "upstreamOf 只能是“应用/服务/容器 是 应用/服务/容器 的上游”";
                    return false;
                }
                return true;

            case "deployedfrom":
                if (f != "app" && f != "service" && f != "container")
                {
                    error = "deployedFrom 的起点必须是应用/服务/容器（from=app:/service:/container:）";
                    return false;
                }
                if (t != "deploy")
                {
                    error = "deployedFrom 的终点必须是部署记录（to=deploy:...）";
                    return false;
                }
                return true;

            case "partof":
                if (f != "app" && f != "service" && f != "container")
                {
                    error = "partOf 的起点必须是应用/服务/容器（from=app:/service:/container:）";
                    return false;
                }
                if (t != "app" && t != "service" && t != "system")
                {
                    error = "partOf 的终点必须是应用/服务/系统（to=app:/service:/system:）";
                    return false;
                }
                return true;

            case "relatedto":
                return true;

            case "accessedvia":
                if (f != "ssh" && f != "ds")
                {
                    error = "accessedVia 的起点必须是服务器或数据源（from=ssh:/ds:）";
                    return false;
                }
                if (t != "ssh")
                {
                    error = "accessedVia 的终点必须是服务器（to=ssh:）";
                    return false;
                }
                return true;

            default:
                error = $"未知关系类型 '{type}'（可用: {string.Join(" / ", KnownTypes)}）";
                return false;
        }
    }

    /// <summary>
    /// 在基础校验之上，结合**已有关系**做语义校验：
    /// 一个应用/数据库（runsOn 的起点）**只能运行在一台服务器**上，不得再 runsOn 另一台。
    /// </summary>
    public static bool TryValidate(string? from, string? to, string? type, IReadOnlyList<RelationConfig> existing, out string? error)
    {
        if (!TryValidate(from, to, type, out error))
            return false;

        if (string.Equals(type?.Trim(), "runsOn", StringComparison.OrdinalIgnoreCase))
        {
            var source = from?.Trim();
            var target = to?.Trim();
            var conflict = existing.FirstOrDefault(r =>
                string.Equals(r.Type, "runsOn", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.From, source, StringComparison.Ordinal) &&
                !string.Equals(r.To, target, StringComparison.Ordinal));

            if (conflict != null)
            {
                error = $"该节点已 runsOn {conflict.To}；一个应用/数据源/服务/容器只能有一个运行宿主（请先删除旧关系）";
                return false;
            }
        }

        return true;
    }

    /// <summary>返回节点前缀（ssh/ds/app/service/container/deploy/system/domain/port/cert/mq），非已知格式返回 null。</summary>
    public static string? PrefixOf(string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return null;

        var value = nodeId.Trim();
        if (value.StartsWith(AssetNode.SshPrefix, StringComparison.Ordinal)) return "ssh";
        if (value.StartsWith(AssetNode.DsPrefix, StringComparison.Ordinal)) return "ds";
        if (value.StartsWith(AssetNode.AppPrefix, StringComparison.Ordinal)) return "app";
        if (value.StartsWith(AssetNode.ServicePrefix, StringComparison.Ordinal)) return "service";
        if (value.StartsWith(AssetNode.ContainerPrefix, StringComparison.Ordinal)) return "container";
        if (value.StartsWith(AssetNode.DeployPrefix, StringComparison.Ordinal)) return "deploy";
        if (value.StartsWith(AssetNode.SystemPrefix, StringComparison.Ordinal)) return "system";
        if (value.StartsWith(AssetNode.DomainPrefix, StringComparison.Ordinal)) return "domain";
        if (value.StartsWith(AssetNode.PortPrefix, StringComparison.Ordinal)) return "port";
        if (value.StartsWith(AssetNode.CertPrefix, StringComparison.Ordinal)) return "cert";
        if (value.StartsWith(AssetNode.MqPrefix, StringComparison.Ordinal)) return "mq";
        return null;
    }
}
