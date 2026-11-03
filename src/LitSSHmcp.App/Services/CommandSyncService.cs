using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LitSSHmcp.App.Services;

/// <summary>可参与命令同步（输入广播）的会话。仅 SSH 交互式终端实现；本机 PowerShell 不参与。</summary>
public interface ICommandSyncParticipant
{
    /// <summary>用户是否已开启该会话的命令同步。</summary>
    bool IsSyncEnabled { get; set; }

    /// <summary>当前是否已连接（未连接的会话不接收广播）。</summary>
    bool IsConnected { get; }

    /// <summary>把广播输入直接写入本会话，不再二次广播（避免递归）。</summary>
    void SendReplicatedInput(string text);

    event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 命令同步（输入广播）：把任意已开启同步的会话中产生的用户输入，转发给其他已开启同步且已连接的会话。
/// 会话集合变化、连接状态变化、开关变化都会触发 <c>stateChanged</c> 回调，供界面刷新有效数量。
/// </summary>
public sealed class CommandSyncService
{
    private readonly ObservableCollection<object> _sessions;
    private readonly Action? _stateChanged;
    private readonly object _gate = new();
    private readonly HashSet<ICommandSyncParticipant> _participants = new();

    public CommandSyncService(ObservableCollection<object> sessions, Action? stateChanged = null)
    {
        _sessions = sessions;
        _stateChanged = stateChanged;
        _sessions.CollectionChanged += OnSessionsChanged;
        SyncParticipants();
    }

    /// <summary>已开启同步的会话数（可能包含未连接）。</summary>
    public int EnabledCount
    {
        get { lock (_gate) return _participants.Count(p => p.IsSyncEnabled); }
    }

    /// <summary>已开启同步且已连接的有效会话数（本机 PowerShell 不计入）。</summary>
    public int EffectiveCount
    {
        get { lock (_gate) return _participants.Count(p => p.IsSyncEnabled && p.IsConnected); }
    }

    /// <summary>可参与命令同步的会话总数（SSH 终端）。</summary>
    public int ParticipantCount
    {
        get { lock (_gate) return _participants.Count; }
    }

    /// <summary>全部开启或全部关闭（只影响可作为同步参与者的 SSH 终端）。</summary>
    public void SetAll(bool enabled)
    {
        ICommandSyncParticipant[] participants;
        lock (_gate)
            participants = _participants.ToArray();

        foreach (var participant in participants)
            participant.IsSyncEnabled = enabled;
    }

    /// <summary>把一次用户输入广播给其他已开启同步且已连接的会话。</summary>
    public void Broadcast(ICommandSyncParticipant source, string text)
    {
        if (string.IsNullOrEmpty(text) || !source.IsSyncEnabled || !source.IsConnected)
            return;

        ICommandSyncParticipant[] targets;
        lock (_gate)
        {
            targets = _participants
                .Where(p => !ReferenceEquals(p, source) && p.IsSyncEnabled && p.IsConnected)
                .ToArray();
        }

        foreach (var target in targets)
        {
            try { target.SendReplicatedInput(text); }
            catch { /* 单个会话失败不应影响其他会话 */ }
        }
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncParticipants();

    private void SyncParticipants()
    {
        List<ICommandSyncParticipant> removed;
        List<ICommandSyncParticipant> added;
        lock (_gate)
        {
            var current = _sessions.OfType<ICommandSyncParticipant>().ToHashSet();
            removed = _participants.Where(p => !current.Contains(p)).ToList();
            added = current.Where(p => !_participants.Contains(p)).ToList();
            foreach (var participant in removed)
                _participants.Remove(participant);
            foreach (var participant in added)
                _participants.Add(participant);
        }

        foreach (var participant in removed)
            participant.PropertyChanged -= OnParticipantChanged;
        foreach (var participant in added)
            participant.PropertyChanged += OnParticipantChanged;

        if (removed.Count > 0 || added.Count > 0)
            _stateChanged?.Invoke();
    }

    private void OnParticipantChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ICommandSyncParticipant.IsSyncEnabled) or nameof(ICommandSyncParticipant.IsConnected))
            _stateChanged?.Invoke();
    }
}
