using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace LitSSHmcp.Agent;

/// <summary>
/// 把 MCP 的 <see cref="McpClientTool"/>（本质是 <see cref="AIFunction"/>）适配为 <see cref="IAgentTool"/>。
/// <paramref name="displayName"/> 非空时用它作为对外工具名（外部 MCP 与既有工具重名时加服务器前缀）。
/// </summary>
internal sealed class McpAgentTool : IAgentTool
{
    private readonly McpClientTool _tool;
    private readonly AITool _presented;

    public McpAgentTool(McpClientTool tool, bool compactDescription = false, string? displayName = null)
    {
        _tool = tool;
        Name = displayName ?? tool.Name;
        if (string.Equals(Name, tool.Name, StringComparison.Ordinal))
        {
            _presented = compactDescription
                ? new LocalFunction(
                    Name,
                    ToolDescriptions.Compact(tool.Description),
                    tool.JsonSchema.GetRawText(),
                    (args, ct) => tool.InvokeAsync(new AIFunctionArguments(args), ct))
                : tool;
        }
        else
        {
            // AITool.Name 不可改：重名改写时必须重新包一层（描述保持原样/按需精简）
            _presented = new LocalFunction(
                Name,
                compactDescription ? ToolDescriptions.Compact(tool.Description) : tool.Description,
                tool.JsonSchema.GetRawText(),
                (args, ct) => tool.InvokeAsync(new AIFunctionArguments(args), ct));
        }
    }

    public string Name { get; }
    public AITool Tool => _presented;

    public bool Destructive => _tool.ProtocolTool.Annotations?.DestructiveHint == true;

    public bool ReadOnly => _tool.ProtocolTool.Annotations?.ReadOnlyHint == true;

    public string? ToolGroup => ToolFilter.GroupOf(Name);

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct) =>
        await _tool.InvokeAsync(new AIFunctionArguments(arguments), ct);
}
