using LitSSHmcp.Agent;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 桌面会话跨进程对账辅助（评估报告 D1/§4：Web 是独立进程，会写同一个 agent.db）：
/// 用轻量快照判断"会话列表是否变化"，决定是否重载列表 / 当前会话消息。
/// 跨进程只能看到**已落库的整轮结果**（Web 在回合结束才写消息），无法实时流式。
/// </summary>
public static class SessionAutoRefresh
{
    /// <summary>会话列表快照：数量 + 最大 Id + 最新 UpdatedAt（ISO 字符串可直接序比较）。</summary>
    public static string Snapshot(IReadOnlyCollection<AgentSessionRow>? rows)
    {
        if (rows is null || rows.Count == 0)
            return "0";
        var maxId = rows.Max(r => r.Id);
        var latest = rows.Max(r => r.UpdatedAt ?? string.Empty) ?? string.Empty;
        return $"{rows.Count}|{maxId}|{latest}";
    }

    /// <summary>选中会话是否需要重载消息：其 UpdatedAt 与上次加载时不同即需要。</summary>
    public static bool ShouldReloadSelected(string? loadedUpdatedAt, string? currentUpdatedAt)
        => !string.Equals(loadedUpdatedAt ?? string.Empty, currentUpdatedAt ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// 会话列表的可见结构是否相同（Id/标题/分组/排序/模型）；仅 UpdatedAt 变化不算结构变化，
    /// 可原地更新行而不清空重建侧栏，避免任务结束时闪烁。
    /// </summary>
    public static bool SameStructure(IReadOnlyList<AgentSessionRow>? current, IReadOnlyList<AgentSessionRow>? next)
    {
        if (ReferenceEquals(current, next))
            return true;
        if (current is null || next is null || current.Count != next.Count)
            return false;

        for (var i = 0; i < current.Count; i++)
        {
            var a = current[i];
            var b = next[i];
            if (a.Id != b.Id ||
                !string.Equals(a.Title, b.Title, StringComparison.Ordinal) ||
                !string.Equals(a.Group, b.Group, StringComparison.Ordinal) ||
                !string.Equals(a.ProviderId, b.ProviderId, StringComparison.Ordinal) ||
                a.SortOrder != b.SortOrder)
                return false;
        }
        return true;
    }
}
