using LitSSHmcp.Agent;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    private sealed class CachedSessionView
    {
        public string UpdatedAt { get; init; } = string.Empty;
        public List<AgentTurn> Turns { get; init; } = new();
        public bool HasUser { get; init; }
    }

    private readonly Dictionary<long, CachedSessionView> _sessionViewCache = new();
    private bool _restoringCachedTurns;

    private void CacheTranscript(long id, string? updatedAt, List<AgentTurn> turns, bool hasUser)
    {
        if (string.IsNullOrEmpty(updatedAt))
            return;
        _sessionViewCache[id] = new CachedSessionView
        {
            UpdatedAt = updatedAt,
            Turns = turns.ToList(),
            HasUser = hasUser
        };
    }

    private static void CollapseHistoricalTurns(IEnumerable<AgentTurn> turns)
    {
        foreach (var turn in turns)
        {
            turn.ShowTools = turn.HasFailedTools;
            turn.ShowReasoning = false;
        }
    }

    private void InvalidateTranscriptCache(long id)
    {
        if (_sessionViewCache.Count > 0)
            _sessionViewCache.Remove(id);
    }

    /// <summary>
    /// 切换到已加载且没有外部更新的空闲会话时，复用内存中的轮次与运行时，
    /// 避免重复读 agent.db、重建转录和 ResetSession（保留会话上下文与模型侧缓存）。
    /// </summary>
    private async Task<bool> TryRestoreCachedSessionAsync(long id, AgentSessionRow? session)
    {
        if (session is null ||
            !_sessionViewCache.TryGetValue(id, out var cache) ||
            !string.Equals(cache.UpdatedAt, session.UpdatedAt, StringComparison.Ordinal))
        {
            return false;
        }

        var ctx = CurrentContext;
        var rt = ctx.Runtime;
        var provider = SelectedProvider;
        if (rt is null || provider is null ||
            RuntimeRootMismatched ||
            ctx.RuntimeVersion != _poolVersion ||
            !string.Equals(rt.Provider.Id, provider.Id, StringComparison.Ordinal) ||
            !SameSubset(ctx.McpSubset, _sessionMcpSubset))
        {
            return false;
        }

        _restoringCachedTurns = true;
        try
        {
            Turns.Clear();
            foreach (var turn in cache.Turns)
                Turns.Add(turn);
            ctx.LiveTurns = cache.Turns;
        }
        finally
        {
            _restoringCachedTurns = false;
        }

        _sessionTitled = cache.HasUser;
        Plan.Clear();
        foreach (var item in ctx.PlanItems)
            Plan.Add(item);

        ResetTranscriptWindow();
        await RestoreActiveSkillsAsync(id);
        await RestorePlanModeAsync(id);
        await RestoreToolPolicyAsync(id);

        rt.SetActiveSkills(_skills.ActiveIdsOrNull);
        rt.SetActiveAgent(SelectedAgent?.ToPromptContext());
        rt.SetPlanMode(_planMode);
        rt.SetToolPolicy(_toolPolicy);

        ReloadSkillCatalog();
        IsLoading = false;
        ModelStatus = "ready";
        StatusMessage = $"已切换会话 · {provider.DisplayName} · {rt.ToolCount} 个工具";
        RefreshContextInfo();
        return true;
    }

    private static void FinalizeHistoricalTurns(IEnumerable<AgentTurn> turns)
    {
        foreach (var turn in turns)
        {
            if (!turn.IsRunning)
                continue;
            turn.IsRunning = false;
            if (string.IsNullOrEmpty(turn.AssistantText) && string.IsNullOrEmpty(turn.Note))
                turn.Note = "上次运行未正常结束";
        }
    }
    private static bool SameSubset(IReadOnlyCollection<string>? left, IReadOnlyCollection<string>? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        return left.Count == right.Count && left.All(right.Contains);
    }
}
