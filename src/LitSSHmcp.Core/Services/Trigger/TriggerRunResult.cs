using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Trigger;

/// <summary>流水线中一步动作的执行结果。</summary>
public sealed class TriggerActionResult
{
    public TriggerActionType Type { get; set; }
    public bool Success { get; set; }

    /// <summary>成功输出摘要（如提交哈希、备份文件路径、HTTP 状态）。</summary>
    public string Output { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>一次触发事件执行的完整结果（供状态落盘、webhook payload、UI 展示）。</summary>
public sealed class TriggerRunResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "ok";
    public DateTime StartedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public List<TriggerActionResult> Steps { get; set; } = new();
}
