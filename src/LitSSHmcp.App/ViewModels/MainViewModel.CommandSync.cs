using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>命令同步（输入广播）协调器：只统计 SSH 交互式终端，本机 PowerShell 不参与。</summary>
    public CommandSyncService CommandSync { get; }

    /// <summary>已开启同步的会话数（可能包含未连接）。</summary>
    public int CommandSyncEnabledCount => CommandSync.EnabledCount;

    /// <summary>已开启同步且已连接的有效会话数。</summary>
    public int CommandSyncEffectiveCount => CommandSync.EffectiveCount;

    /// <summary>已开启但当前未连接的会话数。</summary>
    public int CommandSyncDisconnectedCount => CommandSync.EnabledCount - CommandSync.EffectiveCount;

    public bool CommandSyncAnyEnabled => CommandSync.EnabledCount > 0;

    /// <summary>是否存在可参与命令同步的 SSH 终端（本机 PowerShell 不算）。</summary>
    public bool HasCommandSyncSessions => CommandSync.ParticipantCount > 0;

    public string CommandSyncToolTip
    {
        get
        {
            var enabled = CommandSync.EnabledCount;
            var effective = CommandSync.EffectiveCount;
            var disconnected = enabled - effective;
            var text = $"命令同步：已开启 {enabled} 个，当前有效 {effective} 个";
            if (disconnected > 0)
                text += $"（{disconnected} 个未连接）";
            if (effective < 2)
                text += "；至少 2 个已连接会话才会实际广播";
            return text + "。本机 PowerShell 不参与。";
        }
    }

    /// <summary>全部开启或全部关闭命令同步（只影响 SSH 交互式终端）。</summary>
    public void SetCommandSyncForAll(bool enabled) => CommandSync.SetAll(enabled);

    private void OnCommandSyncStateChanged()
    {
        OnPropertyChanged(nameof(CommandSyncEnabledCount));
        OnPropertyChanged(nameof(CommandSyncEffectiveCount));
        OnPropertyChanged(nameof(CommandSyncDisconnectedCount));
        OnPropertyChanged(nameof(CommandSyncAnyEnabled));
        OnPropertyChanged(nameof(HasCommandSyncSessions));
        OnPropertyChanged(nameof(CommandSyncToolTip));
    }
}
