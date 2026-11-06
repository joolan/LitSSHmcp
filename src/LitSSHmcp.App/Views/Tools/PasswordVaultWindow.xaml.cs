using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using MenuItem = System.Windows.Controls.MenuItem;

namespace LitSSHmcp.App.Views.Tools;

public partial class PasswordVaultWindow : FluentWindow
{
    private const uint WdaExcludeFromCapture = 0x11;
    private readonly PasswordVaultViewModel _viewModel;
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private readonly List<string> _tempFiles = new();
    private Point _attachmentDragStart;
    private PasswordAttachment? _attachmentDragItem;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    public PasswordVaultWindow(PasswordVaultService? service = null)
    {
        InitializeComponent();
        _viewModel = new PasswordVaultViewModel(service ?? new PasswordVaultService());
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Loaded += OnLoaded;
        UpdateLockPanel();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, WdaExcludeFromCapture);
        }
        catch
        {
            // 旧版 Windows 不支持截屏保护时忽略
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasVault)
            UnlockPassword.Focus();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PasswordVaultViewModel.HasVault) or nameof(PasswordVaultViewModel.IsUnlocked))
            UpdateLockPanel();
    }

    private void UpdateLockPanel()
    {
        var hasVault = _viewModel.HasVault;
        UnlockPanel.Visibility = hasVault ? Visibility.Visible : Visibility.Collapsed;
        CreatePanel.Visibility = hasVault ? Visibility.Collapsed : Visibility.Visible;
        UnlockTitle.Text = hasVault ? "解锁密码保管箱" : "创建密码保管箱";
        UnlockHint.Text = hasVault
            ? "输入独立主密码后查看账号、验证码和附件。"
            : "密码保管箱使用独立主密码，与加密文件柜互不影响。";
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        SearchBox.Clear();
        if (DataContext is PasswordVaultViewModel viewModel)
            viewModel.SearchText = string.Empty;
        e.Handled = true;
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is not { } menu)
            return;
        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    private void OnMoreMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;
        foreach (var item in EnumerateMenuItems(menu.Items))
        {
            if (item.Tag is not string tag)
                continue;
            if (tag.StartsWith("autolock:", StringComparison.Ordinal))
                item.IsChecked = tag["autolock:".Length..] == _viewModel.SelectedAutoLock;
            else if (tag.StartsWith("clipboard:", StringComparison.Ordinal))
                item.IsChecked = tag["clipboard:".Length..] == _viewModel.SelectedClipboardClear;
        }
    }

    private void OnSetAutoLock(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && tag.StartsWith("autolock:", StringComparison.Ordinal))
            _viewModel.SelectedAutoLock = tag["autolock:".Length..];
    }

    private void OnSetClipboardClear(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && tag.StartsWith("clipboard:", StringComparison.Ordinal))
            _viewModel.SelectedClipboardClear = tag["clipboard:".Length..];
    }

    private static IEnumerable<MenuItem> EnumerateMenuItems(ItemCollection items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in EnumerateMenuItems(item.Items))
                yield return child;
        }
    }

    private void OnItemCopyUsername(object sender, RoutedEventArgs e)
    {
        SelectRowFromMenu(sender);
        AsyncOperation.Run("复制用户名", async () => await _viewModel.CopyUsernameAsync());
    }

    private void OnItemCopyPassword(object sender, RoutedEventArgs e)
    {
        SelectRowFromMenu(sender);
        AsyncOperation.Run("复制密码", async () => await _viewModel.CopyPasswordAsync());
    }

    private void OnItemCopyTotp(object sender, RoutedEventArgs e)
    {
        SelectRowFromMenu(sender);
        AsyncOperation.Run("复制验证码", async () => await _viewModel.CopyTotpAsync());
    }

    private void OnItemEdit(object sender, RoutedEventArgs e)
    {
        SelectRowFromMenu(sender);
        OnEditItem(sender, e);
    }

    private void OnItemToggleFavorite(object sender, RoutedEventArgs e)
    {
        SelectRowFromMenu(sender);
        AsyncOperation.Run("切换收藏", async () => await _viewModel.ToggleFavoriteAsync());
    }

    private void OnItemDelete(object sender, RoutedEventArgs e)
    {
        SelectRowFromMenu(sender);
        OnDeleteItem(sender, e);
    }

    private void SelectRowFromMenu(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is PasswordItemRow row)
            _viewModel.SelectedItem = row;
    }

    private void OnChangePassword(object sender, RoutedEventArgs e)
    {
        var dialog = new VaultChangePasswordWindow("密码保管箱",
            (oldPassword, newPassword) => _viewModel.ChangePasswordAsync(oldPassword, newPassword))
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
            _dialogs.Info("主密码已修改，下次解锁请使用新密码。", "密码保管箱");
    }

    private void OnExportPackage(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出密码保管箱加密备份",
            Filter = "密码保管箱备份 (*.lspvault)|*.lspvault|所有文件 (*.*)|*.*",
            FileName = $"密码保管箱-{DateTime.Now:yyyyMMdd}.lspvault",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        AsyncOperation.Run("导出密码保管箱备份", async () =>
        {
            var error = await _viewModel.ExportPackageAsync(dialog.FileName);
            if (error is not null)
                _dialogs.Error(error, "导出失败");
            else
                _dialogs.Info("加密备份已导出。", "密码保管箱");
        });
    }

    private void OnRestorePackage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择密码保管箱备份",
            Filter = "密码保管箱备份 (*.lspvault)|*.lspvault|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var prompt = new VaultPasswordPromptWindow(
            "输入备份包的原主密码，用于验证并恢复。恢复完成后需要重新解锁。")
        {
            Owner = this
        };
        if (prompt.ShowDialog() != true)
            return;

        var password = prompt.Password;
        AsyncOperation.Run("恢复密码保管箱备份", async () =>
        {
            var error = await _viewModel.RestorePackageAsync(dialog.FileName, password);
            if (error is not null)
                _dialogs.Error(error, "恢复失败");
            else
                _dialogs.Info(_viewModel.StatusMessage, "恢复完成");
        });
    }

    private void OnCreateVault(object sender, RoutedEventArgs e)
    {
        var dialog = new VaultCreateWindow(
            "创建密码保管箱",
            "密码保管箱",
            "保管箱名称",
            "主密码无法找回。密码保管箱与加密文件柜使用不同主密码。")
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        AsyncOperation.Run("创建密码保管箱", async () =>
        {
            await _viewModel.CreateAsync(dialog.VaultName, dialog.Password);
        });
    }

    private void OnUnlock(object sender, RoutedEventArgs e) => Unlock();
    private void OnUnlockKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Unlock();
    }

    private void Unlock()
    {
        if (UnlockPassword.Password.Length == 0)
        {
            _viewModel.Touch();
            return;
        }
        var password = UnlockPassword.Password;
        AsyncOperation.Run("解锁密码保管箱", async () =>
        {
            if (await _viewModel.UnlockAsync(password))
            {
                UnlockPassword.Clear();
                UnlockPassword.Focus();
            }
        });
    }

    private void OnLock(object sender, RoutedEventArgs e) => _viewModel.Lock();

    private void OnAddItem(object sender, RoutedEventArgs e)
    {
        var dialog = new PasswordItemEditWindow { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        AsyncOperation.Run("保存账号条目", async () => await _viewModel.SaveItemAsync(dialog.Result));
    }

    private void OnEditItem(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is null)
            return;
        var dialog = new PasswordItemEditWindow(_viewModel.SelectedItem.Model) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        AsyncOperation.Run("保存账号条目", async () => await _viewModel.SaveItemAsync(dialog.Result));
    }

    private void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        var item = _viewModel.SelectedItem;
        if (item is null)
            return;
        if (!_dialogs.Confirm($"确定删除「{item.Title}」？该条目的附件也会一起删除。",
                "删除账号条目", warning: true, defaultNo: true))
            return;
        AsyncOperation.Run("删除账号条目", async () => await _viewModel.DeleteSelectedAsync());
    }

    private void OnTogglePassword(object sender, RoutedEventArgs e) => _viewModel.TogglePassword();

    private void OnCopyUsername(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("复制用户名", async () => await _viewModel.CopyUsernameAsync());

    private void OnCopyPassword(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("复制密码", async () => await _viewModel.CopyPasswordAsync());

    private void OnCopyHistoryPassword(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PasswordHistoryEntry entry)
            return;
        AsyncOperation.Run("复制历史密码", async () => await _viewModel.CopyHistoryPasswordAsync(entry));
    }

    private void OnRestoreHistoryPassword(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PasswordHistoryEntry entry)
            return;
        if (!_dialogs.Confirm("恢复该历史密码？当前密码会加入密码历史。", "恢复历史密码",
                warning: true, defaultNo: true))
            return;
        AsyncOperation.Run("恢复历史密码", async () => await _viewModel.RestoreHistoryPasswordAsync(entry));
    }

    private void OnCopyUrl(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("复制网址", async () => await _viewModel.CopyUrlAsync());

    private void OnCopyTotp(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("复制验证码", async () => await _viewModel.CopyTotpAsync());

    private void OnAdvanceHotp(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("推进 HOTP", async () => await _viewModel.AdvanceHotpAsync());

    private void OnAddAttachment(object sender, RoutedEventArgs e)
    {
        var item = _viewModel.SelectedItem;
        if (item is null)
            return;
        var dialog = new OpenFileDialog { Title = "选择要加密保存的附件", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true)
            return;
        AsyncOperation.Run("加密保存附件", async () => await _viewModel.AddAttachmentAsync(item.Model, dialog.FileName));
    }

    private void OnExportAttachment(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is null || (sender as FrameworkElement)?.DataContext is not PasswordAttachment attachment)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "导出附件（明文）",
            FileName = attachment.Name,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        AsyncOperation.Run("导出附件", async () =>
            await _viewModel.ExportAttachmentAsync(_viewModel.SelectedItem.Model, attachment, dialog.FileName));
    }

    private void OnOpenAttachment(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is null || (sender as FrameworkElement)?.DataContext is not PasswordAttachment attachment)
            return;
        AsyncOperation.Run("临时打开附件", async () => await OpenAttachmentTempAsync(attachment));
    }

    private async Task OpenAttachmentTempAsync(PasswordAttachment attachment)
    {
        var item = _viewModel.SelectedItem?.Model;
        if (item is null)
            return;
        var bytes = await _viewModel.ReadAttachmentBytesAsync(item, attachment);
        var tempDirectory = PasswordVaultTempFiles.DirectoryPath;
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory,
            Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(attachment.Name));

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes);
            CryptographicOperations.ZeroMemory(bytes);
            try { File.SetAttributes(tempPath, FileAttributes.Temporary); } catch { /* ignore */ }
            _tempFiles.Add(tempPath);
            NativeShell.OpenFile(tempPath);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
    }

    private void OnDeleteAttachment(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is null || (sender as FrameworkElement)?.DataContext is not PasswordAttachment attachment)
            return;
        if (!_dialogs.Confirm($"确定删除附件「{attachment.Name}」？", "删除附件",
                warning: true, defaultNo: true))
            return;
        AsyncOperation.Run("删除附件", async () =>
            await _viewModel.DeleteAttachmentAsync(_viewModel.SelectedItem.Model, attachment));
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (_viewModel.IsUnlocked && _viewModel.SelectedItem is not null &&
            e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!_viewModel.IsUnlocked || _viewModel.SelectedItem is null ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;
        e.Handled = true;
        var item = _viewModel.SelectedItem;
        AsyncOperation.Run("添加附件", async () =>
        {
            foreach (var path in paths.Where(File.Exists))
                await _viewModel.AddAttachmentAsync(item.Model, path);
        });
    }

    private void OnAttachmentMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PasswordAttachment attachment)
        {
            _attachmentDragStart = e.GetPosition(this);
            _attachmentDragItem = attachment;
        }
    }

    private void OnAttachmentMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _attachmentDragItem is null)
            return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _attachmentDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _attachmentDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var attachment = _attachmentDragItem;
        _attachmentDragItem = null;
        var item = _viewModel.SelectedItem?.Model;
        if (item is null || sender is not DependencyObject dragSource)
            return;

        AsyncOperation.Run("拖出附件", async () =>
        {
            var bytes = await _viewModel.ReadAttachmentBytesAsync(item, attachment);
            var tempDirectory = PasswordVaultTempFiles.DirectoryPath;
            Directory.CreateDirectory(tempDirectory);
            var tempPath = Path.Combine(tempDirectory,
                Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(attachment.Name));
            try
            {
                await File.WriteAllBytesAsync(tempPath, bytes);
                CryptographicOperations.ZeroMemory(bytes);
                try { File.SetAttributes(tempPath, FileAttributes.Temporary); } catch { /* ignore */ }
                _tempFiles.Add(tempPath);
                var data = new DataObject(DataFormats.FileDrop, new[] { tempPath });
                DragDrop.DoDragDrop(dragSource, data, DragDropEffects.Copy);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
                throw;
            }
        });
    }

    private void OnPreviewAttachment(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is null ||
            (sender as FrameworkElement)?.DataContext is not PasswordAttachment attachment)
            return;
        var extension = Path.GetExtension(attachment.Name).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp"))
        {
            _dialogs.Info("仅支持图片附件预览。", "附件预览");
            return;
        }

        AsyncOperation.Run("预览附件", async () =>
        {
            var bytes = await _viewModel.ReadAttachmentBytesAsync(_viewModel.SelectedItem.Model, attachment);
            var window = new VaultImagePreviewWindow(attachment.Name, bytes) { Owner = this };
            window.ShowDialog();
        });
    }

    private void OnActivity(object sender, MouseButtonEventArgs e) => _viewModel.Touch();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        _viewModel.Touch();
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.F && ctrl)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.N && ctrl)
        {
            if (_viewModel.IsUnlocked)
                OnAddItem(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            _viewModel.SearchText = string.Empty;
            e.Handled = true;
            return;
        }
        if (IsTextInputFocused())
            return;
        if (e.Key == Key.Delete && _viewModel.IsUnlocked)
        {
            OnDeleteItem(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _viewModel.IsUnlocked)
        {
            OnEditItem(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.C && ctrl && _viewModel.IsUnlocked)
        {
            AsyncOperation.Run("复制密码", async () => await _viewModel.CopyPasswordAsync());
            e.Handled = true;
        }
    }

    private static bool IsTextInputFocused() =>
        Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
            or System.Windows.Controls.PasswordBox or ComboBox;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _viewModel.IsUnlocked)
            _viewModel.Lock();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
            _viewModel.Lock();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.Lock();
        _viewModel.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        StateChanged -= OnStateChanged;
        DisposeTempFiles();
    }

    private void DisposeTempFiles()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
        _tempFiles.Clear();
    }
}
