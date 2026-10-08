using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>提供给模型并可被调用的工具（MCP 工具的抽象，便于测试与替换）。</summary>
public interface IAgentTool
{
    string Name { get; }

    /// <summary>是否为破坏性工具（MCP 注解 destructiveHint）；用于 UI 提示“可能等待人工审批”。</summary>
    bool Destructive { get; }

    /// <summary>是否只读（MCP 注解 readOnlyHint；本地工具按其语义标注）。用于「/只读」会话级裁剪。</summary>
    bool ReadOnly { get; }

    /// <summary>所属工具分组（MCP 工具按名推断；本地工具为 null）。用于「/工具」会话级裁剪。</summary>
    string? ToolGroup { get; }

    /// <summary>提供给模型的工具声明（函数签名）。</summary>
    AITool Tool { get; }

    Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct);
}
