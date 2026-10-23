using System.Collections.ObjectModel;
using System.Text.Json;
using LitSSHmcp.Agent;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// AI 助手技能目录与激活状态：扫描技能目录、维护已激活技能 chip、按会话持久化。
/// 运行时的实时切换仍由 AgentViewModel 负责，避免 UI 状态类依赖 AgentRuntime。
/// </summary>
public sealed class AgentSkillState
{
    private readonly Action<string> _notify;
    private readonly List<string> _activeIds = new();

    public AgentSkillState(Action<string> notify) => _notify = notify;

    public ObservableCollection<SkillInfo> AvailableSkills { get; } = new();
    public ObservableCollection<SkillInfo> ActiveSkills { get; } = new();

    public bool HasAvailableSkills => AvailableSkills.Count > 0;
    public bool HasActiveSkills => ActiveSkills.Count > 0;

    /// <summary>当前激活技能 ID（只读快照，供运行时/提示词使用）。</summary>
    public IReadOnlyList<string> ActiveIds => _activeIds;

    /// <summary>运行时需要的参数：无激活技能时返回 null（索引模式）。</summary>
    public IReadOnlyList<string>? ActiveIdsOrNull => _activeIds.Count == 0 ? null : _activeIds;

    public void Reload(string? root, IReadOnlyCollection<string>? availableGroups)
    {
        AvailableSkills.Clear();
        if (root is not null)
        {
            try
            {
                var groups = availableGroups is null ? null : availableGroups.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var info in SkillRegistry.List(root, groups))
                    AvailableSkills.Add(info);
            }
            catch
            {
                // 目录不可读忽略
            }
        }
        _notify(nameof(HasAvailableSkills));
        RebuildChips();
    }

    public async Task RestoreAsync(long sessionId, IContextStore store)
    {
        _activeIds.Clear();
        try
        {
            var json = await store.GetSessionActiveSkillsAsync(sessionId);
            if (!string.IsNullOrWhiteSpace(json))
                _activeIds.AddRange(JsonSerializer.Deserialize<List<string>>(json) ?? new());
        }
        catch
        {
            _activeIds.Clear();
        }
        RebuildChips();
    }

    public void SetActiveIds(IEnumerable<string> ids)
    {
        _activeIds.Clear();
        _activeIds.AddRange(ids.Distinct(StringComparer.OrdinalIgnoreCase));
        RebuildChips();
    }

    public void RemoveActive(string skillId)
    {
        _activeIds.RemoveAll(id => string.Equals(id, skillId, StringComparison.OrdinalIgnoreCase));
        RebuildChips();
    }

    public async Task PersistAsync(long sessionId, IContextStore store)
    {
        if (sessionId <= 0)
            return;
        try
        {
            var json = _activeIds.Count == 0 ? string.Empty : JsonSerializer.Serialize(_activeIds);
            await store.SetSessionActiveSkillsAsync(sessionId, json);
        }
        catch
        {
            // 保存失败不影响主流程
        }
    }

    private void RebuildChips()
    {
        ActiveSkills.Clear();
        foreach (var id in _activeIds)
        {
            var info = AvailableSkills.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (info is not null)
                ActiveSkills.Add(info);
        }
        _notify(nameof(HasActiveSkills));
    }
}
