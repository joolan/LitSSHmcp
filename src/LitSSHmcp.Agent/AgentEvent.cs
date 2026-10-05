namespace LitSSHmcp.Agent;

public enum AgentEventKind
{
    /// <summary>助手文本（最终回答的一段）。</summary>
    AssistantText,
    /// <summary>发起一次工具调用（名称/参数）。</summary>
    ToolCall,
    /// <summary>工具返回（结果文本）。</summary>
    ToolResult,
    /// <summary>本轮结束。</summary>
    Done,
    /// <summary>错误/中止。</summary>
    Error
}

/// <summary>Agent 向 UI 推送的事件（工具轨迹/文本/结束）。</summary>
public sealed record AgentEvent(
    AgentEventKind Kind,
    string? Text = null,
    string? ToolName = null,
    string? ArgumentsJson = null,
    bool ToolSuccess = true,
    long DurationMs = 0,
    bool Destructive = false);
