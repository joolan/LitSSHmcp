using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using LitSSHmcp.App.Services;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using LitSSHmcp.App.Services.Toolbox;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class HostsEditorWindow : FluentWindow, INotifyPropertyChanged
{
    private readonly HostsEditorService _service = new();
    private readonly IUserDialogService _dialogs = new UserDialogService();

    public HostsEditorWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => AsyncOperation.Run("读取 Hosts", ReloadAsync);
    }

    public string Path => _service.HostsPath;

    private string _statusMessage = "正在读取…";
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public string LineCountText => $"共 {Editor.LineCount} 行";

    private async Task ReloadAsync()
    {
        try
        {
            Editor.Text = await _service.ReadAsync();
            StatusMessage = "已读取 " + Path;
            OnPropertyChanged(nameof(LineCountText));
        }
        catch (Exception ex)
        {
            StatusMessage = "读取失败：" + ex.Message;
        }
    }

    private void OnEditorTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => OnPropertyChanged(nameof(LineCountText));

    private void OnReload(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("读取 Hosts", ReloadAsync);

    private void OnBackup(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("备份 Hosts", async () =>
        {
            var path = await _service.BackupAsync();
            StatusMessage = path is null ? "备份失败：hosts 文件不存在。" : "已备份到 " + path;
        });

    private void OnFlushDns(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("刷新 DNS", async () =>
        {
            StatusMessage = await _service.FlushDnsAsync() ? "DNS 缓存已刷新。" : "刷新 DNS 失败。";
        });

    private void OnSave(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("保存 Hosts", async () =>
        {
            var result = await _service.SaveAsync(Editor.Text);
            StatusMessage = result.Message;
            if (result.Success)
            {
                var backup = result.BackupPath is null ? string.Empty : "\n\n备份：" + result.BackupPath;
                _dialogs.Info(result.Message + backup, "Hosts 修改工具");
            }
            else
            {
                _dialogs.Warn(result.Message, "Hosts 修改工具");
            }
        });

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
