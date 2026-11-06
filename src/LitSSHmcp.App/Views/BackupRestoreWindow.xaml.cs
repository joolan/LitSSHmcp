using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Backup;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views;

public partial class BackupRestoreWindow : FluentWindow
{
    private readonly BackupRestoreService _service = new();
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private readonly IConfigService _configService = AppServiceFactory.CreateConfigService();
    private BackupArchive? _archive;

    public BackupRestoreWindow(string? initialPath = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(initialPath))
            ArchivePathBox.Text = initialPath;
        Closed += (_, _) => _archive?.Dispose();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择备份文件",
            Filter = "加密备份 (*.lsbak)|*.lsbak|Zip 备份 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            _archive?.Dispose();
            _archive = null;
            ItemsList.ItemsSource = null;
            ArchivePathBox.Text = dialog.FileName;
        }
    }

    private void OnInspect(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("读取备份信息", async () =>
        {
            _archive?.Dispose();
            _archive = null;
            ItemsList.ItemsSource = null;
            StatusText.Text = "正在读取…";
            try
            {
                _archive = await _service.OpenAsync(ArchivePathBox.Text.Trim(), PasswordBoxInput.Password);
                foreach (var item in _archive.Manifest.Items)
                    item.IsSelected = true;
                ItemsList.ItemsSource = _archive.Manifest.Items;
                ArchiveInfoText.Text = $"{_archive.Manifest.PlanName} · " +
                                       $"{_archive.Manifest.CreatedAt:yyyy-MM-dd HH:mm:ss} · " +
                                       $"{_archive.EntryCount} 个文件";
                WarningText.Text = _archive.Manifest.Warning;
                StatusText.Text = BackupCrypto.IsEncrypted(ArchivePathBox.Text.Trim())
                    ? "已读取加密备份，请选择恢复目录。"
                    : "已读取备份，请选择恢复目录。";
            }
            catch (Exception ex)
            {
                StatusText.Text = "读取失败：" + ex.Message;
                _dialogs.Error(ex.Message, "读取备份失败");
            }
        });

    private void OnRestoreModeChanged(object sender, RoutedEventArgs e) =>
        RestoreDirectoryPicker.IsEnabled = RestoreToDataCheckBox.IsChecked != true;

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_archive is null)
        {
            _dialogs.Info("请先读取备份信息。", "恢复备份");
            return;
        }
        var direct = RestoreToDataCheckBox.IsChecked == true;
        var target = RestoreDirectoryPicker.Value?.Trim() ?? string.Empty;
        if (!direct && target.Length == 0)
        {
            _dialogs.Info("请选择恢复目录。", "恢复备份");
            return;
        }
        var selected = _archive.Manifest.Items
            .Where(item => item.IsSelected)
            .Select(item => item.Id)
            .ToArray();
        if (selected.Length == 0)
        {
            _dialogs.Info("请至少选择一个恢复项目。", "恢复备份");
            return;
        }

        AsyncOperation.Run("恢复备份", async () =>
        {
            try
            {
                if (direct)
                {
                    var config = await _configService.LoadConfigAsync();
                    var result = await _service.RestoreToDataDirectoryAsync(_archive, config,
                        ConfigPaths.AppDataDir, selected);
                    StatusText.Text = result.Message;
                    if (result.Success)
                        _dialogs.Info(result.Message +
                                      (result.SafetyDirectory is null
                                          ? string.Empty
                                          : "\n\n安全备份：" + result.SafetyDirectory),
                            "恢复完成");
                    else
                        _dialogs.Error(result.Message, "恢复失败");
                }
                else
                {
                    var count = await _service.RestoreToDirectoryAsync(_archive, target, selected);
                    StatusText.Text = $"已恢复 {count} 个文件到 {target}";
                    _dialogs.Info($"已恢复 {count} 个文件。\n\n{target}\n\n" +
                                  "当前版本恢复到指定目录，不会直接覆盖正在运行的数据目录。",
                        "恢复完成");
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "恢复失败：" + ex.Message;
                _dialogs.Error(ex.Message, "恢复失败");
            }
        });
    }
}
