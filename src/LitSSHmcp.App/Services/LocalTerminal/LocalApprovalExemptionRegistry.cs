namespace LitSSHmcp.App.Services.LocalTerminal;

/// <summary>
/// 本机终端审批豁免登记表（进程内、随 AI 会话窗口生命周期）：记录用户在审批弹窗勾选的
/// 「本轮次内允许全部命令 / 本次会话期间允许全部命令」授权，命中即跳过弹窗。
/// 只跳过审批弹窗——命令白名单与危险拦截（<see cref="LocalCommandPolicy"/>）仍然生效；
/// 「总是允许此命令」仍写入配置持久化，本表只做会话级内存授权（重启即失效）。
/// 会话级授权按会话键隔离（评估报告 §4.1 并行会话：A 会话的授权不放行 B 会话的命令）。
/// </summary>
public sealed class LocalApprovalExemptionRegistry
{
    private readonly object _gate = new();
    private string? _roundAll;
    /// <summary>「本次会话允许全部」按会话键登记的会话集合。</summary>
    private readonly HashSet<string> _sessionAll = new(StringComparer.Ordinal);

    /// <summary>登记「本轮次内允许全部命令」。roundId 为空则拒绝登记（fail-safe：降级为仅本次）。</summary>
    public bool GrantRoundAll(string? roundId)
    {
        if (string.IsNullOrWhiteSpace(roundId))
            return false;

        lock (_gate)
            _roundAll = roundId.Trim();
        return true;
    }

    /// <summary>登记「本次 AI 会话期间允许全部命令」；会话键为空不登记（fail-safe：只会多弹窗，不误放行）。</summary>
    public void GrantSessionAll(string? sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
            return;
        lock (_gate)
            _sessionAll.Add(sessionKey.Trim());
    }

    /// <summary>
    /// 预检：当前是否可跳过审批——会话级要求键命中（并行会话各记各的）；
    /// 轮次级要求已登记的轮次 ID 与当前轮次一致且均非空（轮次 AsyncLocal + ID 唯一，天然不串号；
    /// 轮次信号缺失只会多弹窗，不会误放行）。
    /// </summary>
    public bool IsApproved(string? currentRoundId, string? sessionKey)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(sessionKey) && _sessionAll.Contains(sessionKey.Trim()))
                return true;

            return !string.IsNullOrEmpty(currentRoundId)
                   && string.Equals(_roundAll, currentRoundId, StringComparison.Ordinal);
        }
    }

    /// <summary>是否已对该会话设置「本次会话期间允许全部命令」（供审批弹窗提示）。</summary>
    public bool SessionAllGranted(string? sessionKey)
    {
        lock (_gate)
            return !string.IsNullOrEmpty(sessionKey) && _sessionAll.Contains(sessionKey.Trim());
    }
}
