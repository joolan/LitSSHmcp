using LitSSHmcp.Core.Models;
using ModelContextProtocol.Client;

namespace LitSSHmcp.Agent;

/// <summary>按配置裁剪暴露给模型的工具（只读模式 + 允许的工具分组）。</summary>
public static class ToolFilter
{
    /// <summary>内部工具名：审批轮次边界（Agent 每条用户消息开始时调用，不暴露给模型）。</summary>
    public const string InternalRoundTool = "mcp_approval_round";

    public static List<McpClientTool> Apply(IEnumerable<McpClientTool> tools, AgentConfig config)
    {
        // 内部工具一律不给模型（Agent 在工具过滤前单独捕获引用做轮次通知）
        var result = tools
            .Where(t => !string.Equals(t.Name, InternalRoundTool, StringComparison.Ordinal))
            .ToList();

        if (config.ReadOnly)
            result = result.Where(IsReadOnly).ToList();

        if (config.AllowedToolGroups is { Length: > 0 })
        {
            var allowed = new HashSet<string>(config.AllowedToolGroups, StringComparer.OrdinalIgnoreCase);
            result = result.Where(t => GroupOf(t.Name) is not { } g || allowed.Contains(g)).ToList();
        }

        return result;
    }

    /// <summary>依据 MCP 工具注解判断是否只读（annotations.readOnlyHint == true）。</summary>
    public static bool IsReadOnly(McpClientTool tool) => tool.ProtocolTool.Annotations?.ReadOnlyHint == true;

    /// <summary>由工具名推断所属工具分组（与 docs/TOOLS.md / config.tools.enabledGroups 的分组键一致）；未知返回 null。</summary>
    public static string? GroupOf(string toolName) => ToolGroups.GroupOf(toolName);
}
