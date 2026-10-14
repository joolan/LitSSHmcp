namespace LitSSHmcp.Agent;

/// <summary>
/// 当前对话轮次（进程内 AsyncLocal）：Agent 每条用户消息开始时更新，
/// 供进程内工具（如本机终端审批）判断「本轮次内放行」授权是否仍有效。
/// 与跨进程的 mcp_approval_round 通报同源（同一次生成的轮次 ID），语义一致。
/// </summary>
public static class ApprovalRoundContext
{
    private static readonly AsyncLocal<string?> Round = new();

    public static string? CurrentRoundId
    {
        get => Round.Value;
        set => Round.Value = value;
    }
}
