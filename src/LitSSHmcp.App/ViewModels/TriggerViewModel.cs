using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Trigger;
using TriggerAction = LitSSHmcp.Core.Models.TriggerAction;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 动作流水线中的一张卡片（包装 <see cref="TriggerAction"/>）：提供类型切换、枚举下拉索引与
/// 各类型字段的显隐通知，供卡片式编辑器绑定。
/// </summary>
public sealed class TriggerActionItem : INotifyPropertyChanged
{
    public TriggerAction Action { get; }
    public TriggerActionItem(TriggerAction action) => Action = action;

    /// <summary>动作类型下拉索引（0=Git 1=Webhook 2=备份 3=数据库备份）。</summary>
    public int TypeIndex
    {
        get => (int)Action.Type;
        set
        {
            if (value < 0) return;
            var type = (TriggerActionType)Math.Clamp(value, 0, 3);
            if (Action.Type == type) return;
            Action.Type = type;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GitVisibility));
            OnPropertyChanged(nameof(WebhookVisibility));
            OnPropertyChanged(nameof(BackupVisibility));
            OnPropertyChanged(nameof(DbBackupVisibility));
            OnPropertyChanged(nameof(TablesText));
        }
    }

    public int GitOperationIndex
    {
        get => (int)Action.GitOperation;
        set { var v = (TriggerGitOperation)Math.Clamp(value, 0, 1); if (Action.GitOperation == v) return; Action.GitOperation = v; OnPropertyChanged(); }
    }

    public int WebhookTemplateIndex
    {
        get => (int)Action.WebhookTemplate;
        set { var v = (TriggerWebhookTemplate)Math.Clamp(value, 0, 3); if (Action.WebhookTemplate == v) return; Action.WebhookTemplate = v; OnPropertyChanged(); }
    }

    public int BackupFormatIndex
    {
        get => (int)Action.BackupFormat;
        set { var v = (TriggerBackupFormat)Math.Clamp(value, 0, 1); if (Action.BackupFormat == v) return; Action.BackupFormat = v; OnPropertyChanged(); }
    }

    /// <summary>指定表文本框与 <see cref="TriggerAction.Tables"/> 的桥接（逗号/换行/分号分隔）。</summary>
    public string TablesText
    {
        get => string.Join(", ", Action.Tables ?? Array.Empty<string>());
        set
        {
            Action.Tables = (value ?? string.Empty)
                .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            OnPropertyChanged();
        }
    }

    public Visibility GitVisibility => Action.Type == TriggerActionType.GitCommitPush ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WebhookVisibility => Action.Type == TriggerActionType.Webhook ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BackupVisibility => Action.Type == TriggerActionType.Backup ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DbBackupVisibility => Action.Type == TriggerActionType.DatabaseBackup ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 触发事件设置：触发事件增删改、目标（本机/远程）、触发源（手动 / 定时 / 被同步任务事件触发）、
/// 动作流水线（git / webhook / 备份，卡片式：每张卡片可编辑、上移、下移、删除）；手动「立即执行」调用 TriggerRunner。
/// </summary>
public sealed class TriggerViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _config = new ConfigService();
    private readonly TriggerRunner _runner = new(AppServiceFactory.CreateSshService(), new AuditLogService(), TriggerStateStore.Default);
    private readonly IUserDialogService _dialogs;
    private bool _suppressOptions;
    private bool _suppressActionSync;

    public TriggerViewModel(IUserDialogService? dialogs = null)
    {
        _dialogs = dialogs ?? new UserDialogService();
        BuildOptions();
        NewTriggerCommand = new RelayCommand(_ => NewTrigger());
        DeleteTriggerCommand = new RelayCommand(_ => _ = DeleteTriggerAsync());
        AddActionCommand = new RelayCommand(_ => AddAction());
        DeleteActionItemCommand = new RelayCommand(p => DeleteActionItem(p as TriggerActionItem));
        MoveUpCommand = new RelayCommand(p => MoveAction(p as TriggerActionItem, -1));
        MoveDownCommand = new RelayCommand(p => MoveAction(p as TriggerActionItem, 1));
        SaveCommand = new RelayCommand(_ => _ = SaveAsync());
        RunNowCommand = new RelayCommand(_ => _ = RunNowAsync());
        _ = LoadAsync();
    }

    public ObservableCollection<SshServerConfig> Servers { get; } = new();
    public ObservableCollection<DataSourceConfig> Datasources { get; } = new();
    public ObservableCollection<TriggerConfig> Triggers { get; } = new();
    public ObservableCollection<TriggerActionItem> ActionItems { get; } = new();
    public ObservableCollection<NumberOption> WeekdayOptions { get; } = new();
    public ObservableCollection<NumberOption> MonthDayOptions { get; } = new();

    public string[] TargetNames { get; } = { "本机", "远程服务器" };
    public string[] ActionTypeNames { get; } = { "Git 提交推送", "Webhook 通知", "目录备份", "数据库备份" };
    public string[] GitOperationNames { get; } = { "仅提交", "提交并推送" };
    public string[] WebhookTemplateNames { get; } = { "通用 JSON", "钉钉", "企业微信", "飞书" };
    public string[] BackupFormatNames { get; } = { "Zip", "TarGz" };

    public ICommand NewTriggerCommand { get; }
    public ICommand DeleteTriggerCommand { get; }
    public ICommand AddActionCommand { get; }
    public ICommand DeleteActionItemCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RunNowCommand { get; }

    private TriggerConfig? _selectedTrigger;
    public TriggerConfig? SelectedTrigger
    {
        get => _selectedTrigger;
        set
        {
            if (!Set(ref _selectedTrigger, value)) return;
            LoadOptionsFromTrigger();
            RebuildActionItems();
            OnPropertyChanged(nameof(TargetIndex));
            OnPropertyChanged(nameof(ServerVisibility));
            OnPropertyChanged(nameof(LocalTargetVisibility));
            OnPropertyChanged(nameof(IsScheduleEnabled));
            OnPropertyChanged(nameof(IsIntervalSchedule));
            OnPropertyChanged(nameof(IsWeeklySchedule));
            OnPropertyChanged(nameof(IsMonthlySchedule));
        }
    }

    // ---- 目标 ----

    public int TargetIndex
    {
        get => SelectedTrigger is null ? 0 : (int)SelectedTrigger.TargetType;
        set
        {
            if (SelectedTrigger is null || value < 0) return;
            if ((int)SelectedTrigger.TargetType == value) return;
            SelectedTrigger.TargetType = (TriggerTarget)value;
            // 切换目标（本机 ↔ 远程）后，原有的本机/远程路径不再适用，清空让用户重新选择
            foreach (var item in ActionItems)
            {
                item.Action.GitDir = string.Empty;
                item.Action.SourcePath = string.Empty;
                item.Action.DestDir = string.Empty;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ServerVisibility));
            OnPropertyChanged(nameof(LocalTargetVisibility));
        }
    }

    public Visibility ServerVisibility =>
        SelectedTrigger?.TargetType == TriggerTarget.Remote ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>目标为本机时显示本地路径选择按钮（预留，现所有目标均可点选择）。</summary>
    public Visibility LocalTargetVisibility =>
        SelectedTrigger?.TargetType == TriggerTarget.Remote ? Visibility.Collapsed : Visibility.Visible;

    // ---- 调度 ----

    public bool IsScheduleEnabled
    {
        get => SelectedTrigger?.ScheduleEnabled ?? false;
        set
        {
            if (SelectedTrigger is null) return;
            SelectedTrigger.ScheduleEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIntervalSchedule));
            OnPropertyChanged(nameof(IsWeeklySchedule));
            OnPropertyChanged(nameof(IsMonthlySchedule));
        }
    }

    public bool IsIntervalSchedule
    {
        get => SelectedTrigger?.ScheduleType == SyncScheduleType.Interval;
        set { if (value) SetSchedule(SyncScheduleType.Interval); }
    }
    public bool IsWeeklySchedule
    {
        get => SelectedTrigger?.ScheduleType == SyncScheduleType.Weekly;
        set { if (value) SetSchedule(SyncScheduleType.Weekly); }
    }
    public bool IsMonthlySchedule
    {
        get => SelectedTrigger?.ScheduleType == SyncScheduleType.Monthly;
        set { if (value) SetSchedule(SyncScheduleType.Monthly); }
    }

    // ---- 动作流水线（卡片式） ----

    private void RebuildActionItems()
    {
        _suppressActionSync = true;
        ActionItems.Clear();
        foreach (var action in SelectedTrigger?.Actions ?? Array.Empty<TriggerAction>())
            ActionItems.Add(new TriggerActionItem(action));
        _suppressActionSync = false;
    }

    /// <summary>把卡片顺序/增删同步回模型的动作数组。</summary>
    private void SyncActionsToModel()
    {
        if (_suppressActionSync || SelectedTrigger is null) return;
        SelectedTrigger.Actions = ActionItems.Select(i => i.Action).ToArray();
    }

    private void AddAction()
    {
        if (SelectedTrigger is null) { StatusMessage = "请先新建一个触发事件。"; return; }
        ActionItems.Add(new TriggerActionItem(new TriggerAction { Type = TriggerActionType.Webhook }));
        SyncActionsToModel();
    }

    private void DeleteActionItem(TriggerActionItem? item)
    {
        if (SelectedTrigger is null || item is null) return;
        ActionItems.Remove(item);
        SyncActionsToModel();
    }

    private void MoveAction(TriggerActionItem? item, int delta)
    {
        if (SelectedTrigger is null || item is null) return;
        var idx = ActionItems.IndexOf(item);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= ActionItems.Count) return;
        ActionItems.Move(idx, target);
        SyncActionsToModel();
    }

    // ---- 结果弹窗 ----

    public event Action<string, string, string, bool>? ResultRequested;

    private void Result(string title, bool success, string message) =>
        ResultRequested?.Invoke(title, success ? "操作成功" : "操作失败", message, !success);

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    // ---- 选项 ----

    private void BuildOptions()
    {
        string[] weekLabels = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        for (var d = 0; d < 7; d++)
        {
            var opt = new NumberOption { Value = d, Label = weekLabels[d] };
            opt.Changed = OnWeekDaysChanged;
            WeekdayOptions.Add(opt);
        }
        for (var d = 1; d <= 28; d++)
        {
            var opt = new NumberOption { Value = d, Label = d.ToString() };
            opt.Changed = OnMonthDaysChanged;
            MonthDayOptions.Add(opt);
        }
    }

    private void LoadOptionsFromTrigger()
    {
        _suppressOptions = true;
        var weeks = SelectedTrigger?.WeekDays ?? Array.Empty<int>();
        var months = SelectedTrigger?.MonthDays ?? Array.Empty<int>();
        foreach (var o in WeekdayOptions) o.Selected = weeks.Contains(o.Value);
        foreach (var o in MonthDayOptions) o.Selected = months.Contains(o.Value);
        _suppressOptions = false;
    }

    private void OnWeekDaysChanged()
    {
        if (_suppressOptions || SelectedTrigger is null) return;
        SelectedTrigger.WeekDays = WeekdayOptions.Where(o => o.Selected).Select(o => o.Value).OrderBy(v => v).ToArray();
    }

    private void OnMonthDaysChanged()
    {
        if (_suppressOptions || SelectedTrigger is null) return;
        SelectedTrigger.MonthDays = MonthDayOptions.Where(o => o.Selected).Select(o => o.Value).OrderBy(v => v).ToArray();
    }

    private void SetSchedule(SyncScheduleType type)
    {
        if (SelectedTrigger is null) return;
        SelectedTrigger.ScheduleType = type;
        OnPropertyChanged(nameof(IsIntervalSchedule));
        OnPropertyChanged(nameof(IsWeeklySchedule));
        OnPropertyChanged(nameof(IsMonthlySchedule));
    }

    // ---- 数据 ----

    private async Task LoadAsync()
    {
        try
        {
            var config = await _config.LoadConfigAsync();
            Servers.Clear();
            foreach (var s in config.Servers)
                Servers.Add(s);

            Datasources.Clear();
            foreach (var ds in config.DataSources)
            {
                if (ds.Type.Equals("mysql", StringComparison.OrdinalIgnoreCase) ||
                    ds.Type.Equals("postgres", StringComparison.OrdinalIgnoreCase))
                    Datasources.Add(ds);
            }

            Triggers.Clear();
            foreach (var t in config.Triggers)
            {
                var state = TriggerStateStore.Default.Get(t.Id);
                if (state is not null)
                {
                    t.LastRunAt = state.LastRunAt;
                    t.LastResult = state.LastResult;
                }
                Triggers.Add(t);
            }

            SelectedTrigger = Triggers.FirstOrDefault();
            if (SelectedTrigger is null)
                NewTrigger();
            StatusMessage = $"已加载 {Triggers.Count} 个触发事件（仅 App 运行期间生效）";
        }
        catch (Exception ex)
        {
            StatusMessage = "加载失败: " + ex.Message;
        }
    }

    private void NewTrigger()
    {
        var trigger = new TriggerConfig
        {
            Name = "新建触发事件",
            TargetType = TriggerTarget.Local,
            ServerId = Servers.FirstOrDefault()?.Id,
            Actions = new[]
            {
                new TriggerAction { Type = TriggerActionType.GitCommitPush, GitOperation = TriggerGitOperation.CommitAndPush }
            }
        };
        Triggers.Add(trigger);
        SelectedTrigger = trigger;
    }

    private async Task DeleteTriggerAsync()
    {
        if (SelectedTrigger is null) { StatusMessage = "当前没有选中的触发事件。"; return; }
        if (!_dialogs.Confirm($"确定删除触发事件「{SelectedTrigger.Name}」？", "删除"))
            return;
        Triggers.Remove(SelectedTrigger);
        SelectedTrigger = Triggers.FirstOrDefault();
        await SaveToConfigAsync();
    }

    private async Task SaveAsync()
    {
        if (SelectedTrigger is null) { StatusMessage = "请先新建一个触发事件。"; Result("保存触发事件", false, StatusMessage); return; }
        SyncActionsToModel();
        var errors = SelectedTrigger.Validate();
        if (errors.Count > 0) { StatusMessage = errors[0]; Result("保存触发事件", false, string.Join("\n", errors)); return; }
        await SaveToConfigAsync();
    }

    private async Task SaveToConfigAsync()
    {
        try
        {
            var config = await _config.LoadConfigAsync();
            config.Triggers = Triggers.ToArray();
            await _config.SaveConfigAsync(config);
            StatusMessage = "已保存（定时/同步联动仅在 App 运行期间生效）";
            Result("保存触发事件", true, StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = "保存失败: " + ex.Message;
            Result("保存触发事件", false, StatusMessage);
        }
    }

    private async Task RunNowAsync()
    {
        if (SelectedTrigger is null) { StatusMessage = "请先新建一个触发事件。"; Result("立即执行", false, StatusMessage); return; }
        SyncActionsToModel();
        var trigger = SelectedTrigger;
        var errors = trigger.Validate();
        if (errors.Count > 0) { StatusMessage = errors[0]; Result("立即执行", false, string.Join("\n", errors)); return; }

        try
        {
            StatusMessage = "正在执行…";
            var config = await _config.LoadConfigAsync();
            var result = await _runner.RunAsync(trigger, TriggerRunCause.Manual, config, CancellationToken.None);

            trigger.LastRunAt = result.StartedAt;
            trigger.LastResult = result.Message;
            RefreshTriggerItem(trigger);

            var detail = string.Join("\n", result.Steps.Select((s, i) =>
                $"{i + 1}. {TriggerNotice.ShortType(s.Type)}: {(s.Success ? "✓" : "✗")}（{s.Duration.TotalSeconds:F1}s）" +
                (string.IsNullOrWhiteSpace(s.Success ? s.Output : s.Error) ? "" : "\n   " + (s.Success ? s.Output : s.Error))));
            StatusMessage = result.Success ? "执行成功" : "执行失败：" + result.Message;
            Result("立即执行", result.Success, string.IsNullOrWhiteSpace(detail) ? result.Message : detail);
        }
        catch (Exception ex)
        {
            StatusMessage = "执行失败: " + ex.Message;
            Result("立即执行", false, StatusMessage);
        }
    }

    private void RefreshTriggerItem(TriggerConfig trigger)
    {
        var idx = Triggers.IndexOf(trigger);
        if (idx < 0) return;
        Triggers.RemoveAt(idx);
        Triggers.Insert(idx, trigger);
        SelectedTrigger = trigger;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
