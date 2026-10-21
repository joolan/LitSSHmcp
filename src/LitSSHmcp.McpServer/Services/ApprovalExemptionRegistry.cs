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
/// 审批入口预检命中即自动放行。授权键包含工具、目标、完整命令/操作与文件路径，
/// 因此对 A 服务器（或某条命令）的放行不会扩散到 B 服务器或另一条命令。
/// stdio 模式下每个 MCP 客户端连接是独立进程，故授权天然以「会话」为界，进程退出即全部失效。
/// </summary>
public sealed class ApprovalExemptionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<GrantKey, string> _round = new(GrantKeyComparer.Instance);
    private readonly HashSet<GrantKey> _session = new(GrantKeyComparer.Instance);

    /// <summary>登记「本轮次」授权。缺少工具/目标/命令或 roundId 为空时拒绝登记（fail-safe：降级为仅本次）。</summary>
    public bool GrantRound(string? toolName, string? target, string? command, string? filePath, string? roundId)
    {
        var key = CreateKey(toolName, target, command, filePath);
        if (key is null || string.IsNullOrWhiteSpace(roundId))
            return false;

        lock (_gate)
            _round[key] = roundId.Trim();
        return true;
    }

    /// <summary>登记「整个会话」授权；缺少工具/目标/命令时拒绝登记。</summary>
    public bool GrantSession(string? toolName, string? target, string? command, string? filePath)
    {
        var key = CreateKey(toolName, target, command, filePath);
        if (key is null)
            return false;

        lock (_gate)
            _session.Add(key);
        return true;
    }

    /// <summary>
    /// 预检：该工具 + 目标 + 完整操作当前是否可自动放行——会话级恒放行；
    /// 轮次级要求已登记的轮次 ID 与当前轮次一致且均非空（轮次信号丢失只会多弹窗，不会误放行）。
    /// </summary>
    public bool IsApproved(string? toolName, string? target, string? command, string? filePath, string? currentRoundId)
    {
        var key = CreateKey(toolName, target, command, filePath);
        if (key is null)
            return false;

        lock (_gate)
        {
            if (_session.Contains(key))
                return true;

            return !string.IsNullOrEmpty(currentRoundId)
                   && _round.TryGetValue(key, out var grantedRound)
                   && string.Equals(grantedRound, currentRoundId, StringComparison.Ordinal);
        }
    }

    /// <summary>当前会话级授权的「工具 @ 目标」清单（供审批弹窗提示）。</summary>
    public IReadOnlyList<string> SessionGrantedTools
    {
        get
        {
            lock (_gate)
            {
                return _session
                    .Select(k => k.Display)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(t => t, StringComparer.Ordinal)
                    .ToList();
            }
        }
    }

    private static GrantKey? CreateKey(string? toolName, string? target, string? command, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(toolName)
            || string.IsNullOrWhiteSpace(target)
            || string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        return new GrantKey(toolName.Trim(), target.Trim(), command, filePath ?? string.Empty);
    }

    private sealed record GrantKey(string Tool, string Target, string Command, string FilePath)
    {
        public string Display => $"{Tool} @ {Target}";
    }

    private sealed class GrantKeyComparer : IEqualityComparer<GrantKey>
    {
        public static readonly GrantKeyComparer Instance = new();

        public bool Equals(GrantKey? x, GrantKey? y) =>
            x is not null && y is not null
            && string.Equals(x.Tool, y.Tool, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Target, y.Target, StringComparison.Ordinal)
            && string.Equals(x.Command, y.Command, StringComparison.Ordinal)
            && string.Equals(x.FilePath, y.FilePath, StringComparison.Ordinal);

        public int GetHashCode(GrantKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Tool, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.Target, StringComparer.Ordinal);
            hash.Add(obj.Command, StringComparer.Ordinal);
            hash.Add(obj.FilePath, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
