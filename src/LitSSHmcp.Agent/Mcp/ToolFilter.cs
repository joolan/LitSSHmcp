using LitSSHmcp.Core.Models;
using ModelContextProtocol.Client;

namespace LitSSHmcp.Agent;

/// <summary>按配置裁剪暴露给模型的工具（只读模式 + 允许的工具分组）。</summary>
public static class ToolFilter
{
    public static List<McpClientTool> Apply(IEnumerable<McpClientTool> tools, AgentConfig config)
    {
        var result = tools.ToList();

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
