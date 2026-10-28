using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol;

namespace LitSSHmcp.Agent;

/// <summary>
/// 包装内置 <c>mcp_usage_guide</c>：调用后按当前会话实际可用工具裁剪返回的 JSON，
/// 避免 profile/只读/分组裁剪后仍向模型推荐不存在的工具。工具声明与调用参数保持不变。
/// </summary>
internal sealed class GuideFilterTool : IAgentTool
{
    private readonly IAgentTool _inner;
    private readonly IReadOnlySet<string> _allToolNames;
    private readonly IReadOnlySet<string> _allowedToolNames;

    public GuideFilterTool(IAgentTool inner, IReadOnlySet<string> allToolNames,
        IReadOnlySet<string> allowedToolNames)
    {
        _inner = inner;
        _allToolNames = allToolNames;
        _allowedToolNames = allowedToolNames;
    }

    public string Name => _inner.Name;
    public AITool Tool => _inner.Tool;
    public bool Destructive => _inner.Destructive;
    public bool ReadOnly => _inner.ReadOnly;
    public string? ToolGroup => _inner.ToolGroup;

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct) =>
        Filter(await _inner.InvokeAsync(arguments, ct));

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments,
        IProgress<ProgressNotificationValue>? progress, CancellationToken ct) =>
        Filter(await _inner.InvokeAsync(arguments, progress, ct));

    private object? Filter(object? result)
    {
        var text = result switch
        {
            null => null,
            string value => value,
            JsonElement element => element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.GetRawText(),
            JsonNode node => node is JsonValue value && value.TryGetValue<string>(out var valueText)
                ? valueText
                : node.ToJsonString(),
            _ => result.ToString()
        };

        if (text is null)
            return result;

        var filtered = GuideResultFilter.Filter(text, _allToolNames, _allowedToolNames);
        return filtered ?? result;
    }
}
