using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>提供给模型并可被调用的工具（MCP 工具的抽象，便于测试与替换）。</summary>
public interface IAgentTool
{
    string Name { get; }

    /// <summary>是否为破坏性工具（MCP 注解 destructiveHint）；用于 UI 提示“可能等待人工审批”。</summary>
    bool Destructive { get; }

    /// <summary>提供给模型的工具声明（函数签名）。</summary>
    AITool Tool { get; }

    Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct);
}
