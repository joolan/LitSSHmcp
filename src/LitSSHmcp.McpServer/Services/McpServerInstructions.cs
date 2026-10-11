using System.Text;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// MCP <c>initialize</c> 返回的 server instructions（会话级、随每次连接交给模型的行为约定）。
/// 保持精简且**不枚举具体工具名**；但会**按启用分组动态生成**——只提及实际暴露的能力域，
/// 并明确列出被停用的域，避免模型调用不存在的工具。完整的"意图→工具"路由由 <c>mcp_usage_guide</c> 提供。
/// </summary>
public static class McpServerInstructions
{
    public static string Build(IReadOnlySet<string> enabled)
    {
        var sb = new StringBuilder();
        sb.Append("LitSSH MCP: 在本机通过 SSH 运维服务器、访问 MySQL/PostgreSQL/Redis 数据源, 并做跨机故障排查。\n");
        sb.Append("使用约定:\n");

        var families = ToolGroups.All.Where(enabled.Contains).Select(ToolGroups.Display).ToArray();
        sb.Append($"1) 按意图选组: {(families.Length > 0 ? string.Join("; ", families) : "（本部署未启用任何工具组）")}。\n");

        var idParts = new List<string>();
        if (enabled.Contains(ToolGroups.Ssh)) idParts.Add("serverId 来自 ssh_list_servers");
        if (enabled.Contains(ToolGroups.Datasource)) idParts.Add("datasourceId 来自 datasource_list");
        if (idParts.Count > 0)
            sb.Append($"2) ID 获取: {string.Join(", ", idParts)}。\n");

        if (enabled.Contains(ToolGroups.Guide))
            sb.Append("3) 不确定用哪个工具时, 先调用 mcp_usage_guide 获取当前可用工具清单与\"意图→工具\"路由表。\n");

        sb.Append("4) 写操作(SQL 写 / Redis 写 / 文件传输 / 敏感命令)会弹桌面确认; 被拒绝会返回 status=rejected, 不要重试轰炸。\n");
        sb.Append("5) 所有入参/出参都不含密码; 命令与 SQL 均写入本机审计库。");

        var disabled = ToolGroups.All.Where(g => !enabled.Contains(g)).ToArray();
        if (disabled.Length > 0)
            sb.Append($"\n注意: 本部署未启用以下能力: {string.Join(", ", disabled.Select(ToolGroups.Display))}。"
                      + "对应的工具不存在, 不要调用; 如确需使用, 请提示用户到桌面 App「设置 → 工具分组」开启后重连。");

        return sb.ToString();
    }
}
