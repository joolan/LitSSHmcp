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

    private void OnDeleteSelected(object sender, RoutedEventArgs e) => _ = DeleteSelectedAsync();

    private async Task DeleteSelectedAsync()
    {
        var selected = BackupList.SelectedItems.OfType<string>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "请先选择要删除的备份（可按住 Ctrl/Shift 多选）。";
            return;
        }

        var names = selected
            .Select(System.IO.Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();
        var preview = string.Join("\n", names.Take(8));
        if (names.Count > 8)
            preview += $"\n… 共 {names.Count} 个";

        if (!_dialogs.Confirm(
                $"永久删除以下历史备份？\n\n{preview}\n\n删除后无法恢复。",
                "删除历史备份"))
            return;

        var failures = new List<string>();
        foreach (var backup in selected)
        {
            try
            {
                var result = await _vm.DeleteBackupAsync(_tab, backup);
                if (!result.Success)
                {
                    var reason = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
                    failures.Add($"{System.IO.Path.GetFileName(backup)}: {reason.Trim()}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{System.IO.Path.GetFileName(backup)}: {ex.Message}");
            }
        }

        await LoadAsync();
        if (failures.Count > 0)
        {
            StatusText.Text =
                $"已删除 {selected.Count - failures.Count} 个，失败 {failures.Count} 个：{string.Join("；", failures.Take(3))}";
        }
    }

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
