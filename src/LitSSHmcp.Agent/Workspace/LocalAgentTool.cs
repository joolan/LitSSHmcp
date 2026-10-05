using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>把本地 <see cref="AIFunction"/>（如工作区文档工具）适配为 <see cref="IAgentTool"/>。</summary>
internal sealed class LocalAgentTool : IAgentTool
{
    private readonly AIFunction _function;

    public LocalAgentTool(AIFunction function) => _function = function;

    public string Name => _function.Name;
    public AITool Tool => _function;
    public bool Destructive => false;

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct) =>
        await _function.InvokeAsync(new AIFunctionArguments(arguments), ct);
}
