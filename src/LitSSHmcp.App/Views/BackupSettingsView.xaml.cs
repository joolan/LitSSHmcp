using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Backup;
using LitSSHmcp.App.ViewModels;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views;

public partial class BackupSettingsView : UserControl
{
    private readonly BackupSettingsViewModel _viewModel;
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private bool _loaded;

    public BackupSettingsView()
    {
        InitializeComponent();
        _viewModel = new BackupSettingsViewModel();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        AsyncOperation.Run("加载备份计划", _viewModel.LoadAsync);
    }

    private void OnSaveOss(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("保存 OSS 配置", async () =>
        {
            await _viewModel.SaveOssAsync(OssSecretBox.Password);
            OssSecretBox.Clear();
        });

    private void OnTestOss(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("测试 OSS 连接", async () =>
        {
            var error = await _viewModel.TestOssAsync(OssSecretBox.Password);
            if (error is null)
                _dialogs.Info("OSS 连接成功。", "阿里云 OSS");
            else
                _dialogs.Error(error, "OSS 连接失败");
        });

    private void OnAdd(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("新建备份计划", async () =>
        {
            var config = await _viewModel.GetConfigAsync();
            if (config is null)
            {
                _dialogs.Error("无法加载配置。", "备份计划");
                return;
            }
            var dialog = new BackupPlanEditWindow(null, config, _viewModel.DataDir)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true)
                return;
            await _viewModel.SavePlanAsync(dialog.Result);
        });

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPlan is not { } row)
        {
            _dialogs.Info("请选择要编辑的备份计划。", "备份计划");
            return;
        }
        AsyncOperation.Run("编辑备份计划", async () =>
        {
            var config = await _viewModel.GetConfigAsync();
            if (config is null)
                return;
            var dialog = new BackupPlanEditWindow(row.Plan, config, _viewModel.DataDir)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true)
                return;
            await _viewModel.SavePlanAsync(dialog.Result);
        });
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPlan is not { } row)
            return;
        if (!_dialogs.Confirm($"确定删除备份计划「{row.Name}」？已生成的备份文件不会被删除。",
                "删除备份计划", warning: true, defaultNo: true))
            return;
        AsyncOperation.Run("删除备份计划", () => _viewModel.DeletePlanAsync(row));
    }

    private void OnHistory(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPlan is not { } row)
        {
            _dialogs.Info("请选择要查看历史的备份计划。", "备份历史");
            return;
        }
        new BackupHistoryWindow(row.Plan) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OnOssBackups(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("打开 OSS 备份", async () =>
        {
            var config = await _viewModel.GetConfigAsync();
            if (config is null)
            {
                _dialogs.Error("无法读取配置。", "OSS 备份");
                return;
            }
            new OssBackupBrowserWindow(config)
            {
                Owner = Window.GetWindow(this)
            }.ShowDialog();
        });

    private void OnRestore(object sender, RoutedEventArgs e) =>
        new BackupRestoreWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void OnPlanRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is { } item &&
            item.DataContext is BackupPlanRow row)
            _viewModel.SelectedPlan = row;
    }

    private void OnPlanDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedPlan is not null)
            OnEdit(sender, e);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T result)
                return result;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _viewModel.CancelRun();

    private void OnInstallTask(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPlan is not { } row)
        {
            _dialogs.Info("请选择要安装计划任务的备份计划。", "Windows 计划任务");
            return;
        }
        AsyncOperation.Run("安装 Windows 计划任务", async () =>
        {
            await WindowsScheduledTaskService.InstallAsync(row.Plan);
            _dialogs.Info("已安装 Windows 计划任务。App 关闭后也会按计划执行。", "Windows 计划任务");
        });
    }

    private void OnRemoveTask(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPlan is not { } row)
        {
            _dialogs.Info("请选择要移除计划任务的备份计划。", "Windows 计划任务");
            return;
        }
        AsyncOperation.Run("移除 Windows 计划任务", async () =>
        {
            await WindowsScheduledTaskService.RemoveAsync(row.Plan);
            _dialogs.Info("已移除 Windows 计划任务。", "Windows 计划任务");
        });
    }

    private void OnRunNow(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPlan is not { } row)
        {
            _dialogs.Info("请选择要立即备份的计划。", "备份计划");
            return;
        }
        AsyncOperation.Run("立即备份", async () =>
        {
            var result = await _viewModel.RunNowAsync(row);
            if (result.Success)
                _dialogs.Info(result.Message + (result.LocalPath is null ? string.Empty : "\n\n" + result.LocalPath),
                    "备份完成");
            else
                _dialogs.Error(result.Message, "备份失败");
        });
    }
}
