using System.IO;
using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Backup;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views;

public partial class OssBackupBrowserWindow : FluentWindow
{
    private readonly AppConfig _appConfig;
    private readonly BackupOssConfig _config;
    private readonly AliyunOssBackupUploader _oss = new();
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private readonly DpapiSecretProtector _protector = new();
    private IReadOnlyList<OssBackupItem> _items = Array.Empty<OssBackupItem>();

    public OssBackupBrowserWindow(AppConfig config)
    {
        InitializeComponent();
        _appConfig = config;
        _config = config.Backup.Oss;
        EndpointText.Text = string.IsNullOrWhiteSpace(_config.Endpoint)
            ? "未配置 OSS Endpoint"
            : _config.Endpoint;
        Loaded += (_, _) => AsyncOperation.Run("加载 OSS 备份", RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.Endpoint) ||
            string.IsNullOrWhiteSpace(_config.Bucket) ||
            string.IsNullOrWhiteSpace(_config.AccessKeyId) ||
            string.IsNullOrWhiteSpace(_config.AccessKeySecretProtected))
        {
            _items = Array.Empty<OssBackupItem>();
            OssList.ItemsSource = _items;
            SummaryText.Text = "OSS 配置不完整";
            StatusText.Text = "请先在备份设置中保存 Endpoint、Bucket、AccessKeyId 和 AccessKeySecret。";
            return;
        }

        try
        {
            StatusText.Text = "正在读取 OSS 备份…";
            var secret = _protector.Unprotect(_config.AccessKeySecretProtected) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("AccessKeySecret 无法解密，请重新填写并保存。");
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var globalPrefix = (_config.Prefix ?? string.Empty).Trim().Trim('/');
            if (!string.IsNullOrWhiteSpace(globalPrefix))
                prefixes.Add(globalPrefix);
            foreach (var plan in _appConfig.Backup.Plans)
            {
                if (!string.IsNullOrWhiteSpace(plan.OssPrefixOverride))
                    prefixes.Add(plan.OssPrefixOverride.Trim().Trim('/'));
                else if (string.IsNullOrWhiteSpace(globalPrefix))
                    prefixes.Add(plan.Id);
            }
            if (prefixes.Count == 0)
                prefixes.Add(string.Empty);
            var results = await Task.WhenAll(prefixes.Select(prefix =>
                _oss.ListAsync(_config, secret, prefix)));
            _items = results.SelectMany(items => items)
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(item => item.LastModified)
                .ToArray();
            OssList.ItemsSource = _items;
            SummaryText.Text = $"{_items.Count} 个 OSS 备份 · 合计 " +
                               BackupFormat.Size(_items.Sum(item => item.Size));
            StatusText.Text = "只读列表，不提供删除 OSS 对象功能。";
        }
        catch (Exception ex)
        {
            _items = Array.Empty<OssBackupItem>();
            OssList.ItemsSource = _items;
            SummaryText.Text = "读取 OSS 备份失败";
            StatusText.Text = ex.Message;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("加载 OSS 备份", RefreshAsync);

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if (OssList.SelectedItem is not OssBackupItem item)
        {
            _dialogs.Info("请选择要下载的 OSS 备份。", "OSS 备份");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "下载 OSS 备份",
            FileName = item.FileName,
            Filter = "备份文件 (*.zip;*.lsbak)|*.zip;*.lsbak|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        AsyncOperation.Run("下载 OSS 备份", async () =>
        {
            try
            {
                var secret = _protector.Unprotect(_config.AccessKeySecretProtected) ?? string.Empty;
                StatusText.Text = "正在下载 " + item.FileName + "…";
                await _oss.DownloadAsync(_config, secret, item.Key, dialog.FileName);
                StatusText.Text = "已下载：" + dialog.FileName;
            }
            catch (Exception ex)
            {
                _dialogs.Error("下载 OSS 备份失败：" + ex.Message, "OSS 备份");
                StatusText.Text = "下载失败：" + ex.Message;
            }
        });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
