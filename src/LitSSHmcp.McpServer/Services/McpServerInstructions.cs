namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// MCP <c>initialize</c> 返回的 server instructions（会话级、随每次连接交给模型的行为约定）。
/// 保持精简且**不枚举具体工具名**，避免与工具清单漂移；完整的"意图→工具"路由由 <c>mcp_usage_guide</c> 提供。
/// </summary>
public static class McpServerInstructions
{
    public const string Text =
        "LitSSH MCP: 在本机通过 SSH 运维服务器、访问 MySQL/Redis 数据源, 并做跨机故障排查。\n" +
        "使用约定:\n" +
        "1) 按意图选组: 服务器/命令/文件 → ssh_*; 数据源/数据库 → datasource_*/mysql_*/postgres_*/redis_*; 容器 → docker_*; systemd服务 → service_*; 日志文件 → log_*; JVM → java_*; 拓扑与依赖 → topology_*; 应用整体体检 → app_health_snapshot。\n" +
        "2) ID 获取: serverId 来自 ssh_list_servers, datasourceId 来自 datasource_list。\n" +
        "3) 不确定用哪个工具时, 先调用 mcp_usage_guide 获取完整工具清单与\"意图→工具\"路由表。\n" +
        "4) 写操作(SQL 写 / Redis 写 / 文件传输 / 敏感命令)会弹桌面确认; 被拒绝会返回 status=rejected, 不要重试轰炸。\n" +
        "5) 所有入参/出参都不含密码; 命令与 SQL 均写入本机审计库。";
}
