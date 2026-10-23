using System.Collections.ObjectModel;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentSettingsViewModel
{
    // ===== 记忆管理（浏览/按类别过滤/单条删除/清空全部；清空用于更换 Embeddings 模型等场景） =====

    /// <summary>记忆管理列表（含 raw 会话片段；"助手设置"里的统计按钮只给数字，这里给可操作明细）。</summary>
    public ObservableCollection<MemoryEntry> MemoryEntries { get; } = new();

    private MemoryEntry? _selectedMemoryEntry;
    public MemoryEntry? SelectedMemoryEntry
    {
        get => _selectedMemoryEntry;
        set => Set(ref _selectedMemoryEntry, value);
    }

    /// <summary>类别过滤选项（"全部"= 不限；raw=会话/文档片段，其余为结构化类别）。</summary>
    public IReadOnlyList<string> MemoryKindFilters { get; } =
        new[] { "全部", "task", "script", "asset", "raw" };

    private string _memoryKindFilter = "全部";
    public string MemoryKindFilter
    {
        get => _memoryKindFilter;
        set
        {
            if (_memoryKindFilter == value) return;
            _memoryKindFilter = value;
            OnPropertyChanged();
            _ = LoadMemoryEntriesAsync();   // 切换过滤即时刷新
        }
    }

    /// <summary>加载记忆列表（读当前磁盘 agent.db，不受"是否已保存"影响；单次最多 500 条）。</summary>
    public async Task LoadMemoryEntriesAsync()
    {
        try
        {
            var store = new AgentMemoryStore();
            var kind = string.IsNullOrEmpty(MemoryKindFilter) || MemoryKindFilter == "全部"
                ? null
                : MemoryKindFilter;
            var records = await store.ListAsync(kind, null, 500, includeRaw: true);
            MemoryEntries.Clear();
            foreach (var r in records)
                MemoryEntries.Add(MemoryEntry.From(r));
            StatusMessage = MemoryEntries.Count == 0 ? "记忆库为空" : $"已加载 {MemoryEntries.Count} 条记忆";
        }
        catch (Exception ex)
        {
            StatusMessage = "读取记忆列表失败: " + ex.Message;
        }
    }

    /// <summary>删除选中的一条记忆（含 raw；确认在视图层完成）。</summary>
    public async Task DeleteMemoryEntryAsync(MemoryEntry? entry)
    {
        if (entry is null)
            return;
        try
        {
            var store = new AgentMemoryStore();
            var ok = await store.DeleteByIdAsync(entry.Id);
            if (ok)
                MemoryEntries.Remove(entry);
            StatusMessage = ok
                ? $"已删除 [{entry.Kind}:{(string.IsNullOrEmpty(entry.Key) ? "会话片段" : entry.Key)}]"
                : "未找到该条目（可能已被删除），已刷新列表";
            if (!ok)
                await LoadMemoryEntriesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "删除记忆失败: " + ex.Message;
        }
    }

    // ===== 历史版本 / 回滚 / 归档（本地管理员；每次更新/删除/归档前自动留存快照） =====

    public ObservableCollection<MemoryVersionEntry> MemoryVersions { get; } = new();

    private MemoryVersionEntry? _selectedMemoryVersion;
    public MemoryVersionEntry? SelectedMemoryVersion
    {
        get => _selectedMemoryVersion;
        set => Set(ref _selectedMemoryVersion, value);
    }

    /// <summary>加载选中记忆的历史版本（Version 倒序；首次更新/归档前没有快照）。</summary>
    public async Task LoadMemoryHistoryAsync(MemoryEntry? entry)
    {
        if (entry is null)
            return;
        try
        {
            var store = new AgentMemoryStore();
            var versions = await store.ListVersionsAsync(entry.Id);
            MemoryVersions.Clear();
            foreach (var v in versions)
                MemoryVersions.Add(MemoryVersionEntry.From(v));
            StatusMessage = versions.Count == 0
                ? "该记忆暂无历史版本（首次更新/删除/归档后才会产生快照）"
                : $"已加载 {versions.Count} 个历史版本";
        }
        catch (Exception ex)
        {
            StatusMessage = "读取历史版本失败: " + ex.Message;
        }
    }

    /// <summary>回滚到选中历史版本；当前状态会先归档，回滚本身可撤销。</summary>
    public async Task RollbackMemoryAsync(MemoryEntry? entry, MemoryVersionEntry? version)
    {
        if (entry is null || version is null)
            return;
        try
        {
            var store = new AgentMemoryStore();
            var ok = await store.RestoreVersionAsync(entry.Id, version.Version);
            await LitSSHmcp.Core.Services.Storage.LocalAudit.LogAsync(
                "agent_memory",
                $"MEMORY_ROLLBACK [{entry.Kind}:{entry.Key}] -> v{version.Version}",
                CommandStatus.Executed,
                result: ok ? "已回滚并归档当前状态" : "回滚失败（版本不存在或无权限）");
            if (!ok)
            {
                StatusMessage = "回滚失败：版本不存在或无权限";
                return;
            }
            await LoadMemoryEntriesAsync();
            await LoadMemoryHistoryAsync(entry);
            StatusMessage = $"已回滚 [{entry.Kind}:{entry.Key}] 到 v{version.Version}（回滚前状态已归档）";
        }
        catch (Exception ex)
        {
            StatusMessage = "回滚记忆失败: " + ex.Message;
        }
    }

    /// <summary>归档选中记忆（superseded，不再召回，但保留历史可回滚）。</summary>
    public async Task ArchiveMemoryEntryAsync(MemoryEntry? entry)
    {
        if (entry is null)
            return;
        try
        {
            var store = new AgentMemoryStore();
            var ok = await store.ArchiveByIdAsync(entry.Id);
            await LitSSHmcp.Core.Services.Storage.LocalAudit.LogAsync(
                "agent_memory",
                $"MEMORY_ARCHIVE [{entry.Kind}:{entry.Key}]",
                CommandStatus.Executed,
                result: ok ? "状态已置为 superseded" : "归档失败（条目不存在或无权限）");
            if (!ok)
            {
                StatusMessage = "归档失败：条目不存在或无权限";
                return;
            }
            await LoadMemoryEntriesAsync();
            await LoadMemoryHistoryAsync(entry);
            StatusMessage = $"已归档 [{entry.Kind}:{entry.Key}]，可用「历史版本」回滚恢复。";
        }
        catch (Exception ex)
        {
            StatusMessage = "归档记忆失败: " + ex.Message;
        }
    }

    /// <summary>清空整个记忆库（含 raw；换 Embeddings 模型/维度前使用，需视图层二次确认）。</summary>
    public async Task ClearAllMemoryAsync()
    {
        try
        {
            var store = new AgentMemoryStore();
            await store.ClearAsync();
            MemoryEntries.Clear();
            MemoryStatsText = string.Empty;
            StatusMessage = "记忆库已清空。换新 Embeddings 模型后历史向量不可复用，请重新积累或让 AI 用 ops_memory_write(mode=save)/ops_script_register 重新登记。";
        }
        catch (Exception ex)
        {
            StatusMessage = "清空记忆库失败: " + ex.Message;
        }
    }

}

/// <summary>记忆管理列表行（展示用；数据源为 agent.db 的 agent_memory 表）。</summary>
public sealed class MemoryEntry
{
    public long Id { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    public int Freq { get; init; }
    public string AssetIds { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;

    public string Title => $"[{Kind}:{(string.IsNullOrEmpty(Key) ? "会话片段" : Key)}] ×{Freq} · {Status}";
    public string Meta => $"更新 {UpdatedAt}" +
                          (string.IsNullOrEmpty(AssetIds) ? string.Empty : $" · 资产 {AssetIds}") +
                          (string.IsNullOrEmpty(Tags) ? string.Empty : $" · 标签 {Tags}");
    public string Preview => Content.Length <= 160 ? Content : Content[..160] + "…";

    public static MemoryEntry From(MemoryRecord r) => new()
    {
        Id = r.Id,
        Kind = r.Kind,
        Key = r.Key,
        Content = r.Content,
        Status = r.Status,
        UpdatedAt = r.UpdatedAt,
        Freq = r.Freq,
        AssetIds = r.AssetIds,
        Tags = r.Tags,
    };
}

/// <summary>记忆历史版本行（展示用；每次更新/删除/归档前的快照）。</summary>
public sealed class MemoryVersionEntry
{
    public long MemoryId { get; init; }
    public int Version { get; init; }
    public string Status { get; init; } = string.Empty;
    public string ArchivedAt { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public int Freq { get; init; }
    public string AssetIds { get; init; } = string.Empty;

    public string Title => $"v{Version} · {Status} · 使用 {Freq} 次";
    public string Meta => $"归档 {ArchivedAt}" +
                          (string.IsNullOrEmpty(AssetIds) ? string.Empty : $" · 资产 {AssetIds}");
    public string Preview => Content.Length <= 160 ? Content : Content[..160] + "…";

    public static MemoryVersionEntry From(MemoryVersion v) => new()
    {
        MemoryId = v.MemoryId,
        Version = v.Version,
        Status = v.Status,
        ArchivedAt = v.ArchivedAt,
        Content = v.Content,
        Freq = v.Freq,
        AssetIds = v.AssetIds,
    };
}
