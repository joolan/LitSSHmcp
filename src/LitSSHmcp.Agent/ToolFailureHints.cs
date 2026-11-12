namespace LitSSHmcp.Agent;

/// <summary>
/// 工具失败后的替代策略提示：只给模型可执行的下一步，不改变原始错误文本。
/// 目的是避免模型对同一失败调用反复重试。
/// </summary>
internal static class ToolFailureHints
{
    public static string Append(string toolName, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var hint = Build(toolName, text);
        return hint is null ? text : text.TrimEnd() + "\n[替代策略] " + hint;
    }

    public static string? Build(string toolName, string text)
    {
        if (Has(text, "approval_timeout", "approval_unavailable", "approval_rejected", "rejected", "denied"))
            return "审批未通过或已超时。不要重复调用同一工具；向用户说明阻塞原因并等待指示。";

        if (Has(text, "snapshot_not_found"))
            return "该服务器还没有可用快照；先调用 ssh_snapshot_refresh(serverId=...) 生成一份。";

        if (Has(text, "snapshot_in_progress"))
            return "快照正在采集中；不要重复刷新，稍后用 ssh_snapshot_get(serverId=...) 查询。";

        if (Has(text, "server_not_found", "server_ambiguous", "server_disabled"))
            return "先用 ssh_list_servers 获取准确 serverId，再重试；不要猜 ID。";

        if (Has(text, "datasource_not_found", "datasource_ambiguous"))
            return "先用 datasource_list 获取准确 datasourceId，再重试。";

        if (Has(text, "unknown tool", "tool not found", "tool_not_found"))
            return "先调用 mcp_usage_guide 查看本会话实际可用工具，再选择工具。";

        if (Has(text, "timeout", "timed out", "deadline exceeded"))
            return "缩小目标/范围或适当提高 timeoutSeconds；快照/巡检可先改用 section=... 取单维度，避免重复同一超时调用。";

        if (Has(text, "connection refused", "connection reset", "broken pipe", "temporarily unavailable", "503", "502", "504"))
            return "这通常是瞬时网络/服务错误；已尝试自动重试一次。仍失败请检查目标连通性，不要连续重复调用。";

        if (Has(text, "invalid argument", "missing required", "required parameter", "参数错误", "缺少"))
            return "检查必填参数与类型；服务器/数据源标识先用列表工具确认。";

        if (Has(text, "permission denied", "not permitted", "access denied"))
            return "检查当前账号权限或是否已启用提权；不要用同一参数反复重试。";

        return null;
    }

    private static bool Has(string text, params string[] tokens) =>
        tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
}
