using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace LitSSHmcp.Agent;

/// <summary>把 MCP 的 <see cref="McpClientTool"/>（本质是 <see cref="AIFunction"/>）适配为 <see cref="IAgentTool"/>。</summary>
internal sealed class McpAgentTool : IAgentTool
{
    private readonly McpClientTool _tool;
    private readonly AITool _presented;

    public McpAgentTool(McpClientTool tool, bool compactDescription = false)
    {
        _tool = tool;
        _presented = compactDescription
            ? new LocalFunction(
                tool.Name,
                ToolDescriptions.Compact(tool.Description),
                tool.JsonSchema.GetRawText(),
                (args, ct) => tool.InvokeAsync(new AIFunctionArguments(args), ct))
            : tool;
    }

    public string Name => _tool.Name;
    public AITool Tool => _presented;

    public bool Destructive => _tool.ProtocolTool.Annotations?.DestructiveHint == true;

    public bool ReadOnly => _tool.ProtocolTool.Annotations?.ReadOnlyHint == true;

    public string? ToolGroup => ToolFilter.GroupOf(Name);

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct) =>
        await _tool.InvokeAsync(new AIFunctionArguments(arguments), ct);
}
