using System.ComponentModel;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

/// <summary>审批轮次边界（内部工具）：由 LitSSH 桌面助手在每条用户消息开始时调用。</summary>
[McpServerToolType]
public sealed class ApprovalRoundTools
{
    private readonly ApprovalRoundTracker _tracker;

    public ApprovalRoundTools(ApprovalRoundTracker tracker) => _tracker = tracker;

    [McpServerTool(Name = "mcp_approval_round", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("内部工具：标记一条新的对话轮次开始（由 LitSSH 桌面助手自动调用，其它 MCP 客户端无需使用）")]
    public string SetRound(
        [Description("本轮次标识（由助手生成的随机串）")] string roundId)
    {
        _tracker.CurrentRoundId = string.IsNullOrWhiteSpace(roundId) ? null : roundId.Trim();
        return "ok";
    }
}
