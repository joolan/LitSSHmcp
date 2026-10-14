namespace LitSSHmcp.McpServer.Services;

/// <summary>审批放行范围（弹窗勾选）。</summary>
public enum ApprovalScope
{
    /// <summary>仅放行本次调用（默认）。</summary>
    Once,
    /// <summary>放行到下一条用户消息之前（本轮次内）。</summary>
    Round,
    /// <summary>放行到本次 MCP 会话结束（不建议）。</summary>
    Session
}

/// <summary>当前审批轮次：由 LitSSH 助手在每条用户消息开始时经 mcp_approval_round 更新（进程级单例）。</summary>
public sealed class ApprovalRoundTracker
{
    private readonly object _gate = new();
    private string? _roundId;

    public string? CurrentRoundId
    {
        get { lock (_gate) return _roundId; }
        set { lock (_gate) _roundId = value; }
    }
}

/// <summary>
/// 审批豁免登记表：记录用户在弹窗里勾选的「本轮次 / 整个会话」授权；
/// 审批入口预检命中即自动放行，无需改动任何工具调用点。按工具名（大小写不敏感）粒度登记。
/// stdio 模式下每个 MCP 客户端连接是独立进程，故授权天然以「会话」为界，进程退出即全部失效。
/// </summary>
public sealed class ApprovalExemptionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _round = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _session = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>登记「本轮次」授权。roundId 为空则拒绝登记（fail-safe：降级为仅本次）。</summary>
    public bool GrantRound(string toolName, string? roundId)
    {
        if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(roundId))
            return false;

        lock (_gate)
            _round[toolName.Trim()] = roundId.Trim();
        return true;
    }

    /// <summary>登记「整个会话」授权。</summary>
    public void GrantSession(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return;

        lock (_gate)
            _session.Add(toolName.Trim());
    }

    /// <summary>
    /// 预检：该工具当前是否可自动放行——会话级恒放行；
    /// 轮次级要求已登记的轮次 ID 与当前轮次一致且均非空（轮次信号丢失只会多弹窗，不会误放行）。
    /// </summary>
    public bool IsApproved(string? toolName, string? currentRoundId)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        lock (_gate)
        {
            if (_session.Contains(toolName))
                return true;

            return !string.IsNullOrEmpty(currentRoundId)
                   && _round.TryGetValue(toolName, out var grantedRound)
                   && string.Equals(grantedRound, currentRoundId, StringComparison.Ordinal);
        }
    }

    /// <summary>当前会话级授权的工具清单（供审批弹窗副标题提示）。</summary>
    public IReadOnlyList<string> SessionGrantedTools
    {
        get { lock (_gate) return _session.OrderBy(t => t, StringComparer.Ordinal).ToList(); }
    }
}
