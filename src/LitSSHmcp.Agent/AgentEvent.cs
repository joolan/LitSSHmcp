namespace LitSSHmcp.Agent;

public enum AgentEventKind
{
    /// <summary>助手文本（最终回答的一段）。</summary>
    AssistantText,
    /// <summary>助手思考过程（推理模型输出的 reasoning 增量，仅用于展示，不回喂上下文）。</summary>
    AssistantReasoning,
    /// <summary>发起一次工具调用（名称/参数）。</summary>
    ToolCall,
    /// <summary>工具返回（结果文本）。</summary>
    ToolResult,
    /// <summary>本轮自动召回的记忆（注入上下文前推送给 UI 展示；Text 为召回文本）。</summary>
    MemoryRecall,
    /// <summary>本轮自动检索的资产图谱上下文（命中资产 + 邻域）。</summary>
    AssetGraphContext,
    /// <summary>本轮结束。</summary>
    Done,
    /// <summary>非致命警告（如记忆召回/图谱检索失败），不影响本轮继续。</summary>
    Warning,
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
    bool Destructive = false,
    string? CallId = null);
