using System.Collections.ObjectModel;
using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class RemoteFileHistoryWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly RemoteFileEditorViewModel _vm;
    private readonly RemoteFileEditTab _tab;
    private readonly string _currentContent;
    private readonly IUserDialogService _dialogs = new UserDialogService();

    internal RemoteFileHistoryWindow(
        RemoteFileEditorViewModel vm, RemoteFileEditTab tab, string currentContent)
    {
        InitializeComponent();
        _vm = vm;
        _tab = tab;
        _currentContent = currentContent;
        DataContext = this;
        PathText.Text = tab.RemotePath ?? string.Empty;
        Loaded += (_, _) => _ = LoadAsync();
    }

    public ObservableCollection<string> Backups { get; } = new();
    public bool Restored { get; private set; }

    private string? SelectedBackup => BackupList.SelectedItem as string;

    private async Task LoadAsync()
    {
        try
        {
            Backups.Clear();
            foreach (var path in await _vm.ListBackupsAsync(_tab))
                Backups.Add(path);
            StatusText.Text = Backups.Count == 0 ? "暂无保存备份。" : $"共 {Backups.Count} 个备份。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取备份失败：" + ex.Message;
        }
    }

    private void OnCompare(object sender, RoutedEventArgs e) => _ = CompareAsync();

    private async Task CompareAsync()
    {
        if (SelectedBackup is not { } backup)
            return;
        try
        {
            var content = await _vm.ReadBackupAsync(backup);
            if (!content.Success)
            {
                StatusText.Text = content.Message;
                return;
            }
            RemoteFileDiffWindow.Show(
                this, "历史备份差异", "当前编辑器内容", "备份版本", _currentContent, content.Content);
        }
        catch (Exception ex)
        {
            StatusText.Text = "比较失败：" + ex.Message;
        }
    }

    private void OnRestore(object sender, RoutedEventArgs e) => _ = RestoreAsync();

    private async Task RestoreAsync()
    {
        if (SelectedBackup is not { } backup)
            return;
        if (!_dialogs.Confirm(
                $"确定用此备份覆盖当前服务器文件？\n\n{backup}\n\n当前文件会先被创建新备份。",
                "恢复历史备份"))
            return;

        try
        {
            var result = await _vm.RestoreBackupAsync(_tab, backup);
            if (!result.Success)
            {
                StatusText.Text = "恢复失败：" + (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
                return;
            }
            Restored = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "恢复失败：" + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = Restored;
        Close();
    }
}
