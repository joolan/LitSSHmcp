using System.IO;
using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Backup;
using LitSSHmcp.App.Services.Notifications;
using LitSSHmcp.App.Views.Tools;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views;

public partial class BackupHistoryWindow : FluentWindow
{
    private readonly BackupPlanConfig _plan;
    private readonly BackupRestoreService _restore = new();
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private IReadOnlyList<BackupHistoryItem> _items = Array.Empty<BackupHistoryItem>();

    public BackupHistoryWindow(BackupPlanConfig plan)
    {
        InitializeComponent();
        _plan = plan;
        Title = "备份历史 - " + plan.Name;
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _items = BackupHistoryService.List(_plan);
        HistoryList.ItemsSource = _items;
        SummaryText.Text = $"{_items.Count} 个本地备份 · 合计 " +
                           BackupFormat.Size(_items.Sum(item => item.Size));
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private BackupHistoryItem? SelectedItem => HistoryList.SelectedItem as BackupHistoryItem;

    private void OnRetryOss(object sender, RoutedEventArgs e) => AsyncOperation.Run("重试 OSS 上传", async () =>
    {
        if (SelectedItem is not { } item)
        {
            _dialogs.Info("请选择要重试上传的备份。", "备份历史");
            return;
        }

        var config = await AppServiceFactory.CreateConfigService().LoadConfigAsync();
        var plan = config.Backup.Plans.FirstOrDefault(p => p.Id == _plan.Id);
        if (plan is null)
        {
            _dialogs.Error("当前配置中找不到该备份计划。", "重试 OSS 上传");
            return;
        }
        if (!plan.UploadToOss)
        {
            _dialogs.Info("该备份计划未启用 OSS 上传。", "重试 OSS 上传");
            return;
        }

        var service = new BackupService(new AliyunOssBackupUploader(), new DpapiSecretProtector());
        var result = await service.UploadExistingAsync(plan, config, item.Path);
        StatusText.Text = result.Message;
        if (result.Success)
            NotificationCenterService.Add("备份", plan.Name, "OSS 上传成功。", NotificationSeverity.Success,
                LitSSHmcp.Core.Models.NotificationTypes.BackupSucceeded);
        else
            NotificationCenterService.Add("备份", plan.Name, result.Message, NotificationSeverity.Error,
                LitSSHmcp.Core.Models.NotificationTypes.BackupFailed);
        if (!result.Success)
            _dialogs.Error(result.Message, "重试 OSS 上传失败");
    });

    private void OnVerify(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { } item)
        {
            _dialogs.Info("请选择要校验的备份。", "备份历史");
            return;
        }
        AsyncOperation.Run("校验备份", async () =>
        {
            string? password = null;
            if (item.Encrypted)
            {
                var prompt = new VaultPasswordPromptWindow("输入备份密码，用于校验备份完整性。")
                {
                    Owner = this
                };
                if (prompt.ShowDialog() != true)
                    return;
                password = prompt.Password;
            }
            var result = await _restore.VerifyAsync(item.Path, password);
            if (result.Success)
                _dialogs.Info($"{result.Message}\n\n文件数：{result.EntryCount}\nSHA256：{result.Sha256}",
                    "校验通过");
            else
                _dialogs.Error(result.Message, "校验失败");
        });
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { } item)
        {
            _dialogs.Info("请选择要恢复的备份。", "备份历史");
            return;
        }
        new BackupRestoreWindow(item.Path) { Owner = this }.ShowDialog();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { } item)
            return;
        if (!_dialogs.Confirm($"删除本地备份「{item.FileName}」？", "删除备份",
                warning: true, defaultNo: true))
            return;
        try
        {
            File.Delete(item.Path);
            Refresh();
            StatusText.Text = "已删除本地备份。";
        }
        catch (Exception ex)
        {
            _dialogs.Error(ex.Message, "删除备份失败");
        }
    }
}
