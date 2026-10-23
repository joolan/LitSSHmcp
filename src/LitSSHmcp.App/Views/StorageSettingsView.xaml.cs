using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class StorageSettingsView : UserControl
{
    private readonly StorageSettingsViewModel _viewModel = new();
    private readonly UserDialogService _dialogs = new();

    public StorageSettingsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += (_, _) => AsyncOperation.Run("刷新存储统计", _viewModel.RefreshAsync);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("刷新存储统计", _viewModel.RefreshAsync);

    private void OnPruneMemoryHistory(object sender, RoutedEventArgs e)
    {
        if (_dialogs.Confirm(
                "清理全部 Memory 历史快照？\n\n每记忆保留最近 40 版，并删除超过 30 天的孤儿快照。",
                "清理记忆历史", defaultNo: true))
            AsyncOperation.Run("清理记忆历史", _viewModel.PruneMemoryHistoryAsync);
    }

    private void OnPruneSpills(object sender, RoutedEventArgs e)
    {
        if (_dialogs.Confirm(
                "按 agent.spillRetentionDays 清理过期的大结果 spill 文件？\n\n仅清理默认 spills 根目录，不递归子目录。",
                "清理过期 spill", defaultNo: true))
            AsyncOperation.Run("清理过期 spill", _viewModel.PruneSpillsAsync);
    }

    private void OnPruneAuditArchive(object sender, RoutedEventArgs e)
    {
        if (_dialogs.Confirm(
                "按 audit.retentionDays 删除过期审计归档？\n\n删除后会按剩余记录重建哈希链；此操作不可恢复，建议先备份 audit.db。",
                "清理审计归档", defaultNo: true))
            AsyncOperation.Run("清理审计归档", _viewModel.PruneAuditArchiveAsync);
    }

    private void OnPruneOrphanSessions(object sender, RoutedEventArgs e)
    {
        if (_dialogs.Confirm(
                "按 agent.orphanSessionDirRetentionDays 清理工作区 jobs 下已无会话引用的目录？\n\n此操作会删除目录内文件，建议先备份。",
                "清理孤儿会话目录", defaultNo: true))
            AsyncOperation.Run("清理孤儿会话目录", _viewModel.PruneOrphanSessionsAsync);
    }
}
