using LitSSHmcp.Agent;

namespace LitSSHmcp.App.ViewModels;

/// <summary>会话/分组拖拽排序的纯计算逻辑（无 UI 依赖，便于单测）。</summary>
public static class AgentSessionOrder
{
    /// <summary>
    /// 计算会话移动到 <paramref name="group"/> 后的排序值：<paramref name="after"/> 为 null 表示置于最前，
    /// 否则紧跟在该会话之后（与相邻项取中值）。
    /// </summary>
    public static double ComputeMoveOrder(
        IEnumerable<AgentSessionRow> sessions,
        AgentSessionRow dragged,
        string group,
        AgentSessionRow? after)
    {
        var siblings = sessions
            .Where(s => s.Id != dragged.Id && string.Equals(s.Group ?? string.Empty, group, StringComparison.Ordinal))
            .OrderBy(s => s.SortOrder).ThenByDescending(s => s.UpdatedAt, StringComparer.Ordinal)
            .ToList();

        if (after is null)
            return siblings.Count > 0 ? siblings[0].SortOrder - 1 : 0;

        var idx = siblings.FindIndex(s => s.Id == after.Id);
        if (idx < 0)
            return siblings.Count > 0 ? siblings[^1].SortOrder + 1 : 0;

        var lo = siblings[idx].SortOrder;
        var hi = idx + 1 < siblings.Count ? siblings[idx + 1].SortOrder : lo + 2;
        return (lo + hi) / 2;
    }

    /// <summary>把分组 <paramref name="dragged"/> 移到 <paramref name="target"/> 之前；无效输入返回 null。</summary>
    public static List<string>? ReorderGroups(IEnumerable<string> groups, string dragged, string target)
    {
        if (string.IsNullOrEmpty(dragged) || string.Equals(dragged, target, StringComparison.Ordinal))
            return null;

        var list = groups.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        if (!list.Remove(dragged))
            return null;

        var idx = list.IndexOf(target);
        if (idx < 0)
            list.Add(dragged);
        else
            list.Insert(idx, dragged);
        return list;
    }
}
