namespace LitSSHmcp.Core.Models;

/// <summary>一条“用户意图 → 主工具”路由；Groups 为显示该路由所需的工具分组。</summary>
public sealed record ToolRoute(string Intent, string Tools, string[] Groups, string? Note = null);

/// <summary>
/// 统一的 MCP 意图路由表。SystemPromptBuilder、mcp_usage_guide 与文档以此为准，
/// 避免同一意图在不同提示层给出不同建议。
/// </summary>
public static class ToolRoutingCatalog
{
    public static IReadOnlyList<ToolRoute> Routes { get; } = new[]
    {
        new ToolRoute("服务器整体态势 / 健康 / 端口 / 账号",
            "`ssh_snapshot_get`；需要最新数据用 `ssh_snapshot_refresh`；变化用 `ssh_snapshot_diff`",
            new[] { ToolGroups.Ssh }, "优先快照，不要逐条拼命令"),
        new ToolRoute("长任务 / 多机操作",
            "`job_start(operation=..., argumentsObject={...}, wait=false)`；查询/取消用 `job_status` / `job_result` / `job_events` / `job_cancel`",
            new[] { ToolGroups.Jobs }, "具体 operation 见 job_operations；大结果先用 job_result(summary=true)，多机一次 job，不要逐台启动"),
        new ToolRoute("命令执行 / Shell / sudo",
            "`ssh_execute_command`；需要 root 时用 `ssh_execute_sudo`",
            new[] { ToolGroups.Command }, "写操作会触发审批"),
        new ToolRoute("日志检索",
            "`log_query`（关键字 / 时间窗 / 文件发现）",
            new[] { ToolGroups.Log }, "不要用无边界 tail -f"),
        new ToolRoute("文件 / 目录",
            "`ssh_list_files`、`ssh_upload`、`ssh_download`",
            new[] { ToolGroups.FileTransfer }, "上传/下载支持文件、目录和批量"),
        new ToolRoute("systemd 服务",
            "`service_query`（状态 / 列表 / 日志）；重启用 `service_restart`",
            new[] { ToolGroups.Service }, null),
        new ToolRoute("Docker 容器",
            "`docker_query`（ps/logs/inspect/stats/images）；写操作走 `docker_restart` / `docker_exec`",
            new[] { ToolGroups.Docker }, null),
        new ToolRoute("Java / JVM",
            "`java_processes` 找进程；`java_diagnostics` 取线程栈 / 堆 / JVM 信息",
            new[] { ToolGroups.Java }, null),
        new ToolRoute("数据源列表 / 连通性",
            "`datasource_list`、`datasource_test_connection`",
            new[] { ToolGroups.Datasource }, "先拿 datasourceId"),
        new ToolRoute("MySQL 查询",
            "只读用 `mysql_query`；写用 `mysql_execute`",
            new[] { ToolGroups.Mysql }, "写操作会触发审批"),
        new ToolRoute("PostgreSQL 查询",
            "只读用 `postgres_query`；写用 `postgres_execute`",
            new[] { ToolGroups.Postgres }, "写操作会触发审批"),
        new ToolRoute("Redis 查询",
            "只读用 `redis_read`；写用 `redis_execute`",
            new[] { ToolGroups.Redis }, "写操作会触发审批"),
        new ToolRoute("拓扑 / 影响面 / 历史",
            "`topology_query`（overview/search/neighbors/path/impact/explain/dependencies/resolve_url）；历史用 `topology_history`",
            new[] { ToolGroups.Topology }, null),
        new ToolRoute("拓扑缺失或过期",
            "`topology_discover`",
            new[] { ToolGroups.Topology }, "同步执行；需要后台/多机长任务时见 jobs 路由"),
        new ToolRoute("应用全链路体检",
            "`app_health_snapshot`",
            new[] { ToolGroups.App }, "先看应用整体，再下钻服务器/数据源"),
        new ToolRoute("不确定用哪个工具 / MCP 自检",
            "`mcp_usage_guide`、`mcp_self_check`",
            new[] { ToolGroups.Guide }, null)
    };

    /// <summary>按当前启用的分组过滤路由；enabled=null 表示全部。</summary>
    public static IReadOnlyList<ToolRoute> ForGroups(IReadOnlyCollection<string>? enabled)
    {
        if (enabled is null)
            return Routes;
        return Routes.Where(route => route.Groups.All(group =>
            enabled.Any(e => string.Equals(e, group, StringComparison.OrdinalIgnoreCase)))).ToArray();
    }
}
