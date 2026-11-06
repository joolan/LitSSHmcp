using System.Windows;
using System.Windows.Input;
using System.IO;
using System.Security.Cryptography;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;
using Wpf.Ui.Controls;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views.Tools;

public partial class EncryptedVaultWindow : FluentWindow
{
    private readonly EncryptedVaultViewModel _viewModel;
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private readonly List<string> _tempFiles = new();

    public EncryptedVaultWindow()
    {
        InitializeComponent();
        _viewModel = new EncryptedVaultViewModel();
        DataContext = _viewModel;
        Loaded += (_, _) => AsyncOperation.Run("加载文件柜", _viewModel.InitializeAsync);
        Closed += (_, _) =>
        {
            DisposeTempFiles();
            _viewModel.Dispose();
        };
        PreviewMouseDown += (_, _) => _viewModel.Touch();
        PreviewKeyDown += (_, _) => _viewModel.Touch();
    }

    private void OnCreateVault(object sender, RoutedEventArgs e)
    {
        var dialog = new VaultCreateWindow { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        AsyncOperation.Run("创建文件柜", async () =>
        {
            if (!await _viewModel.CreateVaultAsync(dialog.VaultName, dialog.Password))
                _dialogs.Warn(_viewModel.StatusMessage, "加密文件柜");
        });
    }

    private void OnUnlock(object sender, RoutedEventArgs e)
    {
        var password = UnlockPassword.Password;
        UnlockPassword.Clear();
        AsyncOperation.Run("解锁文件柜", async () =>
        {
            if (!await _viewModel.UnlockAsync(password))
                _dialogs.Warn(_viewModel.StatusMessage, "加密文件柜");
        });
    }

    private void OnUnlockKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnUnlock(sender, e);
            e.Handled = true;
        }
    }

    private void OnLock(object sender, RoutedEventArgs e)
    {
        DisposeTempFiles();
        UnlockPassword.Clear();
        _viewModel.Lock();
    }

    private void OnTempOpen(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not VaultEntryView entry)
        {
            _dialogs.Warn("请先选择一个文件。", "加密文件柜");
            return;
        }

        if (!_dialogs.Confirm(
                "将把文件临时解密到本机临时目录，并交给系统默认程序打开。\n\n" +
                "风险：\n" +
                "· 临时文件在打开期间以明文存在\n" +
                "· 系统缓存或第三方程序可能保留副本\n" +
                "· 外部程序的修改不会同步回文件柜\n" +
                "· 锁定文件柜会尝试删除临时文件\n\n确定继续？",
                "临时解密打开", warning: true, defaultNo: true))
            return;

        AsyncOperation.Run("临时解密打开", async () => await OpenTempEntryAsync(entry));
    }

    private async Task OpenTempEntryAsync(VaultEntryView entry)
    {
        const long maxBytes = 256L * 1024 * 1024;
        var bytes = await _viewModel.ReadEntryBytesAsync(entry, maxBytes);
        var tempDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LitSSH", "vault-temp");
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory,
            Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(entry.Name));

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes);
            CryptographicOperations.ZeroMemory(bytes);
            try { File.SetAttributes(tempPath, FileAttributes.Temporary); } catch { /* ignore */ }
            _tempFiles.Add(tempPath);
            NativeShell.OpenFile(tempPath);
            _viewModel.SetStatus("已临时解密打开：" + entry.Name);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
    }

    private void DisposeTempFiles()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
        _tempFiles.Clear();
    }

    private void OnCancelOperation(object sender, RoutedEventArgs e) => _viewModel.CancelOperation();

    private void OnExportPackage(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVault is null)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出加密包",
            Filter = "LitSSH 加密包|*.lsvault|所有文件|*.*",
            FileName = _viewModel.SelectedVault.Name + ".lsvault"
        };
        if (dialog.ShowDialog() != true)
            return;

        AsyncOperation.Run("导出加密包", async () => await _viewModel.ExportPackageAsync(dialog.FileName));
    }

    private void OnVaultListRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is not null)
            VaultList.SelectedItem = item.DataContext;
    }

    private void OnVaultSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_viewModel.IsLocked)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            UnlockPassword.Focus();
            Keyboard.Focus(UnlockPassword);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private VaultDescriptor? VaultFromMenu(object sender)
    {
        if (sender is not System.Windows.Controls.MenuItem item)
            return _viewModel.SelectedVault;

        if (item.DataContext is VaultDescriptor direct)
            return direct;
        if (item.Parent is System.Windows.Controls.ContextMenu menu &&
            menu.PlacementTarget is FrameworkElement target &&
            target.DataContext is VaultDescriptor placed)
            return placed;
        return _viewModel.SelectedVault;
    }

    private void OnRenameVault(object sender, RoutedEventArgs e)
    {
        var vault = VaultFromMenu(sender);
        if (vault is null)
            return;

        var name = TextInputDialog.Prompt(this, "重命名文件柜", "新名称：", vault.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        AsyncOperation.Run("重命名文件柜", async () => await _viewModel.RenameVaultAsync(vault, name));
    }

    private void OnChangePassword(object sender, RoutedEventArgs e)
    {
        var vault = VaultFromMenu(sender);
        if (vault is null)
            return;

        var dialog = new VaultChangePasswordWindow(vault.Name,
            (oldPassword, newPassword) => _viewModel.ChangePasswordAsync(vault, oldPassword, newPassword))
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void OnDeleteVault(object sender, RoutedEventArgs e)
    {
        var vault = VaultFromMenu(sender);
        if (vault is null)
            return;

        if (!_dialogs.Confirm($"确定永久删除文件柜「{vault.Name}」？\n\n文件柜内的加密文件会被删除，无法恢复。",
                "删除文件柜", warning: true, defaultNo: true))
            return;

        var dialog = new VaultPasswordPromptWindow($"请输入「{vault.Name}」的主密码以确认删除。") { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        AsyncOperation.Run("删除文件柜", async () => await _viewModel.DeleteVaultAsync(vault, dialog.Password));
    }

    private void OnOpenVaultFolder(object sender, RoutedEventArgs e)
    {
        var vault = VaultFromMenu(sender);
        if (vault is null)
            return;

        NativeShell.OpenFolder(vault.DirectoryPath);
    }

    private void OnCopyVaultPath(object sender, RoutedEventArgs e)
    {
        var vault = VaultFromMenu(sender);
        if (vault is null)
            return;

        try
        {
            Clipboard.SetDataObject(vault.DirectoryPath, true);
            _viewModel.SetStatus("已复制文件柜路径。");
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus("复制路径失败：" + ex.Message);
        }
    }

    private void OnImportPackage(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入加密包",
            Filter = "LitSSH 加密包|*.lsvault|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
            return;

        AsyncOperation.Run("导入加密包", async () => await _viewModel.ImportPackageAsync(dialog.FileName));
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!_viewModel.IsUnlocked || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DropOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!_viewModel.IsUnlocked || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        AsyncOperation.Run("导入文件柜", async () => { await _viewModel.ImportPathsAsync(paths); });
        e.Handled = true;
    }

    private void OnImportFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入到加密文件柜",
            Multiselect = true,
            Filter = "所有文件|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        AsyncOperation.Run("导入文件柜", async () => { await _viewModel.ImportPathsAsync(dialog.FileNames); });
    }

    private void OnImportFolder(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "导入文件夹到加密文件柜",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        AsyncOperation.Run("导入文件柜", async () => { await _viewModel.ImportPathsAsync(new[] { dialog.SelectedPath }); });
    }

    private const long MaxImagePreviewBytes = 32L * 1024 * 1024;
    private const long MaxTextPreviewBytes = 8L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".xml", ".yaml", ".yml", ".csv", ".ini", ".conf", ".cfg",
        ".sh", ".ps1", ".bat", ".cmd", ".cs", ".js", ".ts", ".html", ".css", ".sql", ".py",
        ".go", ".java", ".properties", ".env"
    };

    private void OnEntriesRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject);
        if (item is not null && !item.IsSelected)
        {
            EntriesList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void OnOpenEntryButton(object sender, RoutedEventArgs e) => OpenSelectedEntry();

    private void OnOpenEntry(object sender, MouseButtonEventArgs e) => OpenSelectedEntry();

    private void OpenSelectedEntry()
    {
        if (EntriesList.SelectedItem is not VaultEntryView entry)
        {
            _dialogs.Warn("请先选择一个文件。", "加密文件柜");
            return;
        }

        var extension = Path.GetExtension(entry.RelativePath);
        if (ImageExtensions.Contains(extension))
        {
            OpenImage(entry);
            return;
        }
        if (TextExtensions.Contains(extension))
        {
            OpenText(entry);
            return;
        }

        if (_dialogs.Confirm(
                "该类型暂不支持内存打开。\n\n是否临时解密到本机，并使用系统默认程序打开？\n" +
                "临时文件在打开期间以明文存在，外部程序的修改不会同步回文件柜。",
                "临时解密打开", warning: true, defaultNo: true))
        {
            AsyncOperation.Run("临时解密打开", async () => await OpenTempEntryAsync(entry));
        }
    }

    private void OpenImage(VaultEntryView entry) =>
        AsyncOperation.Run("打开图片", async () =>
        {
            var bytes = await _viewModel.ReadEntryBytesAsync(entry, MaxImagePreviewBytes);
            var window = new VaultImagePreviewWindow(entry.Name, bytes) { Owner = this };
            window.Show();
        });

    private void OpenText(VaultEntryView entry) =>
        AsyncOperation.Run("打开文本", async () =>
        {
            var bytes = await _viewModel.ReadEntryBytesAsync(entry, MaxTextPreviewBytes);
            VaultTextPayload payload;
            try
            {
                payload = VaultTextEncoding.Decode(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            var window = new VaultTextEditorWindow(entry, payload,
                (content, expected) => _viewModel.SaveEntryBytesAsync(entry, content, expected))
            {
                Owner = this
            };
            window.Show();
        });

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var entries = EntriesList.SelectedItems.Cast<VaultEntryView>().ToArray();
        if (entries.Length == 0)
        {
            _dialogs.Warn("请先选择要导出的文件。", "加密文件柜");
            return;
        }

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "导出到目录（导出后为明文）",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        var conflicts = entries
            .Select(e => Path.GetFullPath(Path.Combine(dialog.SelectedPath,
                e.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .ToArray();

        var message = conflicts.Length > 0
            ? $"目标目录中已存在 {conflicts.Length} 个同名文件，继续将覆盖。\n\n" +
              string.Join("\n", conflicts.Take(8).Select(Path.GetFileName)) +
              (conflicts.Length > 8 ? "\n…" : string.Empty) +
              "\n\n导出后的文件将以明文保存，确定继续？"
            : "导出后的文件将以明文保存到目标目录，确定继续？";

        if (!_dialogs.Confirm(message, "导出文件柜", warning: true, defaultNo: true))
            return;

        AsyncOperation.Run("导出文件柜", async () =>
        {
            await _viewModel.ExportAsync(entries, dialog.SelectedPath);
        });
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var entries = EntriesList.SelectedItems.Cast<VaultEntryView>().ToArray();
        if (entries.Length == 0)
        {
            _dialogs.Warn("请先选择要删除的文件。", "加密文件柜");
            return;
        }
        if (!_dialogs.Confirm($"确定从文件柜删除 {entries.Length} 个文件？", "删除文件",
                warning: true, defaultNo: true))
            return;

        AsyncOperation.Run("删除文件", async () => await _viewModel.DeleteAsync(entries));
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        var entry = EntriesList.SelectedItem as VaultEntryView;
        if (entry is null)
        {
            _dialogs.Warn("请先选择一个文件。", "加密文件柜");
            return;
        }

        var name = TextInputDialog.Prompt(this, "重命名文件", "新名称：", entry.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        AsyncOperation.Run("重命名文件", async () => await _viewModel.RenameAsync(entry, name));
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : System.Windows.LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
