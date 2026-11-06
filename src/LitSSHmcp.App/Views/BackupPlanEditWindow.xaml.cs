using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services.Backup;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class BackupPlanEditWindow : FluentWindow
{
    private readonly BackupPlanConfig _plan;
    private readonly bool _isNew;
    private readonly List<BackupItemOption> _itemOptions;

    public BackupPlanEditWindow(BackupPlanConfig? plan, AppConfig config, string dataDir)
    {
        InitializeComponent();
        _isNew = plan is null;
        _plan = plan?.Clone() ?? new BackupPlanConfig();
        TitleBarControl.Title = _isNew ? "新建备份计划" : "编辑备份计划";
        Title = TitleBarControl.Title;

        _itemOptions = BackupItemCatalog.Build(config, dataDir)
            .Select(item => new BackupItemOption
            {
                Item = item,
                IsSelected = _isNew
                    ? item.Id is "config" or "agent-db" or "audit-db" or "snapshot-db" or "skills" or "vaults" or "password-vault"
                    : _plan.Items.Contains(item.Id, StringComparer.Ordinal)
            })
            .ToList();
        ItemsList.ItemsSource = _itemOptions;

        NameBox.Text = _plan.Name;
        EnabledBox.IsChecked = _plan.Enabled;
        ScheduleBox.SelectedIndex = (int)_plan.ScheduleType;
        IntervalBox.Text = Math.Max(1, _plan.IntervalMinutes).ToString();
        TimeBox.Text = _plan.TimeOfDay;
        WeekDaysBox.Text = string.Join(",", _plan.WeekDays);
        MonthDaysBox.Text = string.Join(",", _plan.MonthDays);
        TargetPicker.Value = _plan.TargetDirectory;
        KeepBox.Text = Math.Max(0, _plan.KeepLocalCopies).ToString();
        RetryBox.Text = Math.Max(0, _plan.MaxRetries).ToString();
        UploadOssBox.IsChecked = _plan.UploadToOss;
        EncryptBox.IsChecked = _isNew || _plan.EncryptBackup;
        FallbackBox.IsChecked = _isNew || _plan.FallbackOnStartup;
        OssPrefixBox.Text = _plan.OssPrefixOverride;
        UpdateScheduleVisibility();
    }

    public BackupPlanConfig Result => _plan;

    private void OnScheduleChanged(object sender, SelectionChangedEventArgs e) => UpdateScheduleVisibility();

    private void UpdateScheduleVisibility()
    {
        if (IntervalPanel is null || TimePanel is null || MonthDaysPanel is null)
            return;
        var index = ScheduleBox.SelectedIndex;
        IntervalPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        TimePanel.Visibility = index >= 1 ? Visibility.Visible : Visibility.Collapsed;
        WeekDaysPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        MonthDaysPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (index == 1)
            WeekDaysPanel.Visibility = Visibility.Collapsed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "请填写计划名称。";
            return;
        }
        var target = TargetPicker.Value?.Trim() ?? string.Empty;
        if (target.Length == 0)
        {
            ErrorText.Text = "请选择本地备份目录。";
            return;
        }
        var selected = _itemOptions.Where(item => item.IsSelected).Select(item => item.Id).ToArray();
        if (selected.Length == 0)
        {
            ErrorText.Text = "请至少选择一个备份项目。";
            return;
        }

        _plan.Name = name;
        _plan.Enabled = EnabledBox.IsChecked == true;
        _plan.ScheduleType = (BackupScheduleType)Math.Clamp(ScheduleBox.SelectedIndex, 0, 3);
        _plan.IntervalMinutes = ParsePositive(IntervalBox.Text, 1440);
        _plan.TimeOfDay = string.IsNullOrWhiteSpace(TimeBox.Text) ? "03:00" : TimeBox.Text.Trim();
        _plan.WeekDays = ParseDays(WeekDaysBox.Text, 0, 6, new[] { 1, 2, 3, 4, 5 });
        _plan.MonthDays = ParseDays(MonthDaysBox.Text, 1, 28, new[] { 1 });
        _plan.TargetDirectory = target;
        _plan.KeepLocalCopies = ParseNonNegative(KeepBox.Text, 7);
        _plan.MaxRetries = ParseNonNegative(RetryBox.Text, 1);
        _plan.UploadToOss = UploadOssBox.IsChecked == true;
        _plan.OssPrefixOverride = OssPrefixBox.Text.Trim().Trim('/');
        _plan.EncryptBackup = EncryptBox.IsChecked == true;
        _plan.FallbackOnStartup = FallbackBox.IsChecked == true;
        if (_plan.EncryptBackup)
        {
            if (!string.IsNullOrWhiteSpace(BackupPasswordBox.Password))
            {
                _plan.BackupPasswordProtected =
                    new DpapiSecretProtector().Protect(BackupPasswordBox.Password) ?? string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(_plan.BackupPasswordProtected))
            {
                ErrorText.Text = "请设置备份密码。";
                return;
            }
        }
        else
        {
            _plan.BackupPasswordProtected = string.Empty;
        }
        _plan.Items = selected;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static int ParsePositive(string? text, int fallback) =>
        int.TryParse(text, out var value) && value > 0 ? value : fallback;

    private static int ParseNonNegative(string? text, int fallback) =>
        int.TryParse(text, out var value) && value >= 0 ? value : fallback;

    private static int[] ParseDays(string? text, int min, int max, int[] fallback)
    {
        var values = (text ?? string.Empty)
            .Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out var day) ? Math.Clamp(day, min, max) : int.MinValue)
            .Where(value => value != int.MinValue)
            .Distinct()
            .ToArray();
        return values.Length == 0 ? fallback : values;
    }
}
