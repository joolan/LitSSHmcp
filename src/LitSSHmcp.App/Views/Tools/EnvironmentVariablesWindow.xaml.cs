using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class EnvironmentVariablesWindow : FluentWindow
{
    private readonly EnvironmentVariableService _service = new();
    private readonly UserDialogService _dialogs = new();
    private readonly ObservableCollection<EnvironmentVariableEntry> _userEntries = new();
    private readonly ObservableCollection<EnvironmentVariableEntry> _systemEntries = new();
    private readonly Dictionary<string, EnvironmentVariableEntry> _savedUser =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EnvironmentVariableEntry> _savedSystem =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _systemUnlocked; // 始终先只读；必须显式点击“解锁编辑”后才进入可编辑状态
    private bool _dirtyUser;
    private bool _dirtySystem;

    public EnvironmentVariablesWindow()
    {
        InitializeComponent();
        UserGrid.ItemsSource = _userEntries;
        SystemGrid.ItemsSource = _systemEntries;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private EnvironmentVariableScope CurrentScope =>
        ScopeTabs.SelectedIndex == 1
            ? EnvironmentVariableScope.System
            : EnvironmentVariableScope.User;

    private ObservableCollection<EnvironmentVariableEntry> CurrentEntries =>
        CurrentScope == EnvironmentVariableScope.System ? _systemEntries : _userEntries;

    private System.Windows.Controls.DataGrid CurrentGrid =>
        CurrentScope == EnvironmentVariableScope.System ? SystemGrid : UserGrid;

    private Dictionary<string, EnvironmentVariableEntry> CurrentSaved =>
        CurrentScope == EnvironmentVariableScope.System ? _savedSystem : _savedUser;

    private bool CurrentDirty =>
        CurrentScope == EnvironmentVariableScope.System ? _dirtySystem : _dirtyUser;

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("加载环境变量", LoadAllAsync);

    private async Task LoadAllAsync()
    {
        try
        {
            StatusText.Text = "正在读取环境变量…";
            var user = await Task.Run(() => _service.Read(EnvironmentVariableScope.User));
            var system = await Task.Run(() => _service.Read(EnvironmentVariableScope.System));
            ReplaceEntries(_userEntries, _savedUser, user);
            ReplaceEntries(_systemEntries, _savedSystem, system);
            _dirtyUser = false;
            _dirtySystem = false;
            UpdateUiState();
            StatusText.Text = $"已读取：用户 {_userEntries.Count} 项，系统 {_systemEntries.Count} 项。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取环境变量失败：" + ex.Message;
        }
    }

    private static void ReplaceEntries(
        ObservableCollection<EnvironmentVariableEntry> target,
        Dictionary<string, EnvironmentVariableEntry> saved,
        IEnumerable<EnvironmentVariableEntry> source)
    {
        target.Clear();
        saved.Clear();
        foreach (var entry in source.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(entry);
            saved[entry.Name] = entry.Clone();
        }
    }

    private void OnScopeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ScopeTabs is null)
            return;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        if (AddButton is null || SaveButton is null)
            return;
        var isSystem = CurrentScope == EnvironmentVariableScope.System;
        var editable = !isSystem || _systemUnlocked;
        AddButton.IsEnabled = editable;
        EditButton.IsEnabled = editable;
        DeleteButton.IsEnabled = editable;
        SaveButton.IsEnabled = editable;
        SystemLockBanner.Visibility = isSystem && !_systemUnlocked
            ? Visibility.Visible
            : Visibility.Collapsed;
        UnlockButton.IsEnabled = isSystem && !_systemUnlocked;
    }

    private void MarkDirty()
    {
        if (CurrentScope == EnvironmentVariableScope.System)
            _dirtySystem = true;
        else
            _dirtyUser = true;
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if ((_dirtyUser || _dirtySystem) &&
            !_dialogs.Confirm("刷新会丢弃当前未保存的变更，继续？", "刷新环境变量",
                warning: true, defaultNo: true))
            return;
        AsyncOperation.Run("刷新环境变量", LoadAllAsync);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (!CanEditCurrent())
            return;

        var dialog = new EnvironmentVariableEditWindow { Owner = this };
        dialog.Configure(CurrentScope, null, isNew: true);
        if (dialog.ShowDialog() != true || dialog.Result is not { } entry)
            return;
        if (CurrentEntries.Any(item => string.Equals(item.Name, entry.Name,
                StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.Warn($"环境变量“{entry.Name}”已存在。", "新增环境变量");
            return;
        }

        CurrentEntries.Add(entry);
        CurrentGrid.SelectedItem = entry;
        MarkDirty();
        StatusText.Text = "已添加，点击“保存并应用”写入。";
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (!CanEditCurrent())
            return;
        if (CurrentGrid.SelectedItem is not EnvironmentVariableEntry entry)
        {
            _dialogs.Warn("请先选择一个环境变量。", "编辑环境变量");
            return;
        }

        var dialog = new EnvironmentVariableEditWindow { Owner = this };
        dialog.Configure(CurrentScope, entry, isNew: false);
        if (dialog.ShowDialog() != true || dialog.Result is not { } updated)
            return;
        if (entry.IsCritical &&
            !string.Equals(entry.Name, updated.Name, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.Warn("关键系统变量不允许重命名。", "编辑环境变量");
            return;
        }
        if (CurrentEntries.Any(item => !ReferenceEquals(item, entry) &&
                string.Equals(item.Name, updated.Name, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.Warn($"环境变量“{updated.Name}”已存在。", "编辑环境变量");
            return;
        }

        var index = CurrentEntries.IndexOf(entry);
        CurrentEntries[index] = updated;
        CurrentGrid.SelectedItem = updated;
        MarkDirty();
        StatusText.Text = "已修改，点击“保存并应用”写入。";
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (!CanEditCurrent())
            return;
        if (CurrentGrid.SelectedItem is not EnvironmentVariableEntry entry)
        {
            _dialogs.Warn("请先选择一个环境变量。", "删除环境变量");
            return;
        }
        if (entry.IsCritical)
        {
            _dialogs.Warn($"“{entry.Name}”是关键系统变量，禁止删除。", "删除环境变量");
            return;
        }
        if (!_dialogs.Confirm($"确定删除环境变量“{entry.Name}”？", "删除环境变量",
                warning: true, defaultNo: true))
            return;

        CurrentEntries.Remove(entry);
        MarkDirty();
        StatusText.Text = "已标记删除，点击“保存并应用”写入。";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!CanEditCurrent())
            return;
        AsyncOperation.Run("保存环境变量", SaveAsync);
    }

    private async Task SaveAsync()
    {
        if (!CurrentDirty)
        {
            StatusText.Text = "没有需要保存的变更。";
            return;
        }

        var changes = BuildChanges();
        if (changes.Count == 0)
        {
            StatusText.Text = "没有需要保存的变更。";
            return;
        }

        StatusText.Text = CurrentScope == EnvironmentVariableScope.System
            ? "正在请求管理员权限并写入系统环境变量…"
            : "正在写入用户环境变量…";
        var result = await _service.ApplyAsync(CurrentScope, changes);
        StatusText.Text = result.Message;
        if (!result.Success)
        {
            if (!string.IsNullOrWhiteSpace(result.BackupPath))
                StatusText.Text += " 已创建备份：" + result.BackupPath;
            return;
        }

        var scope = CurrentScope;
        await ReloadScopeAsync(scope);
        StatusText.Text = result.Message +
                          (string.IsNullOrWhiteSpace(result.BackupPath)
                              ? string.Empty
                              : " 备份：" + result.BackupPath);
    }

    private List<EnvironmentVariableChange> BuildChanges()
    {
        var changes = new List<EnvironmentVariableChange>();
        var currentNames = CurrentEntries.Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in CurrentEntries)
        {
            if (CurrentSaved.TryGetValue(entry.Name, out var old) &&
                string.Equals(old.Value, entry.Value, StringComparison.Ordinal) &&
                old.Kind == entry.Kind)
                continue;
            changes.Add(new EnvironmentVariableChange(CurrentScope, entry.Name,
                entry.Value, entry.Kind, Delete: false));
        }

        foreach (var old in CurrentSaved.Values)
        {
            if (currentNames.Contains(old.Name) || EnvironmentVariableRules.IsCritical(old.Name))
                continue;
            changes.Add(new EnvironmentVariableChange(CurrentScope, old.Name, null,
                Microsoft.Win32.RegistryValueKind.String, Delete: true));
        }
        return changes;
    }

    private async Task ReloadScopeAsync(EnvironmentVariableScope scope)
    {
        var entries = await Task.Run(() => _service.Read(scope));
        if (scope == EnvironmentVariableScope.System)
        {
            ReplaceEntries(_systemEntries, _savedSystem, entries);
            _dirtySystem = false;
        }
        else
        {
            ReplaceEntries(_userEntries, _savedUser, entries);
            _dirtyUser = false;
        }
        UpdateUiState();
    }

    private async Task ReloadAllAsync()
    {
        var user = await Task.Run(() => _service.Read(EnvironmentVariableScope.User));
        var system = await Task.Run(() => _service.Read(EnvironmentVariableScope.System));
        ReplaceEntries(_userEntries, _savedUser, user);
        ReplaceEntries(_systemEntries, _savedSystem, system);
        _dirtyUser = false;
        _dirtySystem = false;
        UpdateUiState();
    }

    private void OnBackup(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("备份环境变量", BackupAsync);

    private async Task BackupAsync()
    {
        var result = await _service.CreateBackupAsync(CurrentScope);
        StatusText.Text = result.Message +
                          (string.IsNullOrWhiteSpace(result.BackupPath)
                              ? string.Empty
                              : " " + result.BackupPath);
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (!CanEditCurrent())
            return;
        if (!_dialogs.Confirm(
                $"将当前范围恢复为最近一次备份，并覆盖当前值。\n范围：{CurrentScope}。继续？",
                "恢复环境变量备份", warning: true, defaultNo: true))
            return;
        AsyncOperation.Run("恢复环境变量备份", RestoreAsync);
    }

    private async Task RestoreAsync()
    {
        var scope = CurrentScope;
        StatusText.Text = "正在恢复最近备份…";
        var result = await _service.RestoreLatestAsync(scope);
        StatusText.Text = result.Message;
        if (result.Success)
            await ReloadAllAsync();
    }

    private void OnUnlockSystem(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("解锁系统环境变量", UnlockSystemAsync);

    private async Task UnlockSystemAsync()
    {
        if (_systemUnlocked)
            return;
        UnlockButton.IsEnabled = false;
        StatusText.Text = "等待管理员授权…";
        var alreadyElevated = EnvironmentVariableService.IsElevated;
        try
        {
            var unlocked = await _service.UnlockSystemAsync();
            if (!unlocked)
            {
                StatusText.Text = "未获得管理员权限，系统变量仍为只读。";
                return;
            }
            _systemUnlocked = true;
            UpdateUiState();
            StatusText.Text = alreadyElevated
                ? "当前已具备管理员权限，系统变量已解锁。"
                : "系统变量已解锁；保存时仍会再次提权写入 HKLM。";
        }
        finally
        {
            if (!_systemUnlocked)
                UnlockButton.IsEnabled = true;
        }
    }

    private void OnOpenVerificationTerminal(object sender, RoutedEventArgs e)
    {
        try
        {
            NativeShell.Start(new ProcessStartInfo("cmd.exe",
                "/k \"title 环境变量验证 - 新进程\"")
            {
                UseShellExecute = true
            });
            StatusText.Text = "已打开新终端；请执行 echo %变量名% 验证新值。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "打开终端失败：" + ex.Message;
        }
    }

    private bool CanEditCurrent()
    {
        if (CurrentScope == EnvironmentVariableScope.System && !_systemUnlocked)
        {
            _dialogs.Warn("系统变量为只读；请先点击“解锁编辑（UAC）”。", "环境变量");
            return false;
        }
        return true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if ((_dirtyUser || _dirtySystem) &&
            !_dialogs.Confirm("有未保存的环境变量变更，确定关闭？", "环境变量",
                warning: true, defaultNo: true))
            e.Cancel = true;
    }
}
