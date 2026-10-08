using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Sync;

namespace LitSSHmcp.App.ViewModels;

/// <summary>多选数字项（周几 / 每月几号）。</summary>
public sealed class NumberOption : INotifyPropertyChanged
{
    public required int Value { get; init; }
    public required string Label { get; init; }
    public Action? Changed;

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            Changed?.Invoke();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>文件/文件夹同步设置（单向）：任务增删改、方向联动、调度三选一、文件过滤；保存仅校验必填项（测试为可选自检）。</summary>
public sealed class SyncViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _config = new ConfigService();
    private readonly SyncService _sync = new(new FileSshKnownHostsStore(), SshHostKeyMode.Tofu, new SshService());

    private string? _testedSignature;
    private bool _suppressOptions;

    public SyncViewModel()
    {
        BuildOptions();
        NewTaskCommand = new RelayCommand(_ => NewTask());
        DeleteTaskCommand = new RelayCommand(_ => _ = DeleteTaskAsync());
        TestCommand = new RelayCommand(_ => _ = TestAsync());
        SaveCommand = new RelayCommand(_ => _ = SaveAsync());
        RunNowCommand = new RelayCommand(_ => _ = RunNowAsync());
        _ = LoadAsync();
    }

    public ObservableCollection<SshServerConfig> Servers { get; } = new();
    public ObservableCollection<SyncTaskConfig> Tasks { get; } = new();
    public ObservableCollection<NumberOption> WeekdayOptions { get; } = new();
    public ObservableCollection<NumberOption> MonthDayOptions { get; } = new();

    public ICommand NewTaskCommand { get; }
    public ICommand DeleteTaskCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RunNowCommand { get; }

    private SyncTaskConfig? _selectedTask;
    public SyncTaskConfig? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!Set(ref _selectedTask, value)) return;
            _testedSignature = null;
            LoadOptionsFromTask();
            OnPropertyChanged(nameof(DirectionIndex));
            OnPropertyChanged(nameof(LocalPathVisibility));
            OnPropertyChanged(nameof(SourceVisibility));
            OnPropertyChanged(nameof(TargetVisibility));
            OnPropertyChanged(nameof(IsIntervalSchedule));
            OnPropertyChanged(nameof(IsWeeklySchedule));
            OnPropertyChanged(nameof(IsMonthlySchedule));
            OnPropertyChanged(nameof(TestPassed));
            OnPropertyChanged(nameof(IncludeText));
            OnPropertyChanged(nameof(ExcludeText));
            OnPropertyChanged(nameof(InternalAddressVisibility));
        }
    }

    public string[] DirectionNames { get; } = { "本地 → 远程", "远程 → 本地", "远程 → 远程" };

    public int DirectionIndex
    {
        get => SelectedTask is null ? 0 : (int)SelectedTask.Direction;
        set
        {
            if (SelectedTask is null || value < 0) return;
            SelectedTask.Direction = (SyncDirection)value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LocalPathVisibility));
            OnPropertyChanged(nameof(SourceVisibility));
            OnPropertyChanged(nameof(TargetVisibility));
            OnPropertyChanged(nameof(InternalAddressVisibility));
            InvalidateTest();
        }
    }

    public Visibility LocalPathVisibility =>
        SelectedTask?.Direction is SyncDirection.LocalToRemote or SyncDirection.RemoteToLocal ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SourceVisibility =>
        SelectedTask?.Direction is SyncDirection.RemoteToLocal or SyncDirection.RemoteToRemote ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TargetVisibility =>
        SelectedTask?.Direction is SyncDirection.LocalToRemote or SyncDirection.RemoteToRemote ? Visibility.Visible : Visibility.Collapsed;

    public bool IsIntervalSchedule
    {
        get => SelectedTask?.ScheduleType == SyncScheduleType.Interval;
        set { if (value) SetSchedule(SyncScheduleType.Interval); }
    }
    public bool IsWeeklySchedule
    {
        get => SelectedTask?.ScheduleType == SyncScheduleType.Weekly;
        set { if (value) SetSchedule(SyncScheduleType.Weekly); }
    }
    public bool IsMonthlySchedule
    {
        get => SelectedTask?.ScheduleType == SyncScheduleType.Monthly;
        set { if (value) SetSchedule(SyncScheduleType.Monthly); }
    }

    public bool TestPassed => SelectedTask != null && _testedSignature == SignatureOf(SelectedTask);

    /// <summary>仅远程↔远程显示「使用内网地址」。</summary>
    public Visibility InternalAddressVisibility =>
        SelectedTask?.Direction == SyncDirection.RemoteToRemote ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>仅同步匹配的文件（逗号/分号/空格/换行分隔的 glob）；空=全部。</summary>
    public string IncludeText
    {
        get => SelectedTask is null ? string.Empty : string.Join(", ", SelectedTask.IncludePatterns ?? Array.Empty<string>());
        set
        {
            if (SelectedTask is null) return;
            SelectedTask.IncludePatterns = SplitPatterns(value);
            OnPropertyChanged();
            InvalidateTest();
        }
    }

    /// <summary>排除匹配的文件（同上格式）；空=不排除。</summary>
    public string ExcludeText
    {
        get => SelectedTask is null ? string.Empty : string.Join(", ", SelectedTask.ExcludePatterns ?? Array.Empty<string>());
        set
        {
            if (SelectedTask is null) return;
            SelectedTask.ExcludePatterns = SplitPatterns(value);
            OnPropertyChanged();
            InvalidateTest();
        }
    }

    private static string[] SplitPatterns(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>外部（文件选择/远程浏览）改动了 SelectedTask 的字段后调用：重新读取绑定（模型无 INPC）。</summary>
    public void NotifyEdited()
    {
        InvalidateTest();
        OnPropertyChanged(nameof(SelectedTask));
    }

    /// <summary>请求以模态弹窗显示结果（标题、结论、正文、是否错误）。</summary>
    public event Action<string, string, string, bool>? ResultRequested;

    private void Result(string title, string headline, string message, bool isError) =>
        ResultRequested?.Invoke(title, headline, message, isError);

    private void Result(string title, bool success, string message) =>
        Result(title, success ? "操作成功" : "操作失败", message, !success);

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private string _logText = string.Empty;
    public string LogText { get => _logText; set => Set(ref _logText, value); }

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

    private void LoadOptionsFromTask()
    {
        _suppressOptions = true;
        var weeks = SelectedTask?.WeekDays ?? Array.Empty<int>();
        var months = SelectedTask?.MonthDays ?? Array.Empty<int>();
        foreach (var o in WeekdayOptions) o.Selected = weeks.Contains(o.Value);
        foreach (var o in MonthDayOptions) o.Selected = months.Contains(o.Value);
        _suppressOptions = false;
    }

    private void OnWeekDaysChanged()
    {
        if (_suppressOptions || SelectedTask is null) return;
        SelectedTask.WeekDays = WeekdayOptions.Where(o => o.Selected).Select(o => o.Value).OrderBy(v => v).ToArray();
        InvalidateTest();
    }

    private void OnMonthDaysChanged()
    {
        if (_suppressOptions || SelectedTask is null) return;
        SelectedTask.MonthDays = MonthDayOptions.Where(o => o.Selected).Select(o => o.Value).OrderBy(v => v).ToArray();
        InvalidateTest();
    }

    private void SetSchedule(SyncScheduleType type)
    {
        if (SelectedTask is null) return;
        SelectedTask.ScheduleType = type;
        OnPropertyChanged(nameof(IsIntervalSchedule));
        OnPropertyChanged(nameof(IsWeeklySchedule));
        OnPropertyChanged(nameof(IsMonthlySchedule));
        InvalidateTest();
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
            Tasks.Clear();
            foreach (var t in config.SyncTasks)
            {
                var state = SyncStateStore.Get(t.Id);
                if (state is not null)
                {
                    t.LastRunAt = state.LastRunAt;
                    t.LastResult = state.LastResult;
                }
                Tasks.Add(t);
            }
            SelectedTask = Tasks.FirstOrDefault();
            if (SelectedTask is null)
                NewTask(); // 无任务时自动建一个，保证编辑器可用
            StatusMessage = $"已加载 {Tasks.Count} 个同步任务";
        }
        catch (Exception ex)
        {
            StatusMessage = "加载失败: " + ex.Message;
        }
    }

    private void NewTask()
    {
        var task = new SyncTaskConfig
        {
            Name = "新建同步任务",
            Direction = SyncDirection.LocalToRemote,
            TargetServerId = Servers.FirstOrDefault()?.Id,
            ScheduleType = SyncScheduleType.Interval,
            IntervalMinutes = 60,
            WeekDays = new[] { 1 },
            MonthDays = new[] { 1 },
            MaxRetries = 3
        };
        Tasks.Add(task);
        SelectedTask = task;
    }

    private async Task DeleteTaskAsync()
    {
        if (SelectedTask is null) { StatusMessage = "当前没有选中的同步任务。"; return; }
        if (MessageBox.Show($"确定删除同步任务「{SelectedTask.Name}」？", "删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Tasks.Remove(SelectedTask);
        SelectedTask = Tasks.FirstOrDefault();
        await SaveToConfigAsync();
    }

    private async Task TestAsync()
    {
        if (SelectedTask is null) { StatusMessage = "请先「新建」一个同步任务。"; Result("同步测试", false, StatusMessage); return; }
        var verr = ValidateTask(SelectedTask);
        if (verr is not null) { StatusMessage = verr; Result("同步测试", false, verr); return; }
        try
        {
            StatusMessage = "正在测试（实际传输一个探针文件）…";
            var config = await _config.LoadConfigAsync();
            var result = await _sync.TestAsync(SelectedTask, config, CancellationToken.None);
            StatusMessage = result.Message;
            if (result.Success)
            {
                _testedSignature = SignatureOf(SelectedTask);
                OnPropertyChanged(nameof(TestPassed));
            }
            Result("同步测试", result.Success, result.Message);
        }
        catch (Exception ex)
        {
            StatusMessage = "测试失败: " + ex.Message;
            _testedSignature = null;
            OnPropertyChanged(nameof(TestPassed));
            Result("同步测试", false, StatusMessage);
        }
    }

    private async Task SaveAsync()
    {
        if (SelectedTask is null) { StatusMessage = "请先「新建」一个同步任务。"; Result("保存同步任务", false, StatusMessage); return; }
        var verr = ValidateTask(SelectedTask);
        if (verr is not null) { StatusMessage = verr; Result("保存同步任务", false, verr); return; }
        await SaveToConfigAsync();
    }

    /// <summary>校验任务配置；返回 null 表示通过，否则返回提示。</summary>
    private static string? ValidateTask(SyncTaskConfig t)
    {
        if (string.IsNullOrWhiteSpace(t.Name))
            return "请填写任务名称。";

        switch (t.Direction)
        {
            case SyncDirection.LocalToRemote:
                if (string.IsNullOrWhiteSpace(t.LocalPath)) return "请填写本地路径。";
                if (string.IsNullOrWhiteSpace(t.TargetServerId)) return "请选择目标服务器。";
                if (string.IsNullOrWhiteSpace(t.TargetRemotePath)) return "请填写目标远程路径。";
                break;
            case SyncDirection.RemoteToLocal:
                if (string.IsNullOrWhiteSpace(t.SourceServerId)) return "请选择源服务器。";
                if (string.IsNullOrWhiteSpace(t.SourceRemotePath)) return "请填写源远程路径。";
                if (string.IsNullOrWhiteSpace(t.LocalPath)) return "请填写本地路径。";
                break;
            case SyncDirection.RemoteToRemote:
                if (string.IsNullOrWhiteSpace(t.SourceServerId)) return "请选择源服务器。";
                if (string.IsNullOrWhiteSpace(t.TargetServerId)) return "请选择目标服务器。";
                if (string.Equals(t.SourceServerId, t.TargetServerId, StringComparison.Ordinal))
                    return "远程↔远程同步的「源服务器」与「目标服务器」不能相同。";
                if (string.IsNullOrWhiteSpace(t.SourceRemotePath)) return "请填写源远程路径。";
                if (string.IsNullOrWhiteSpace(t.TargetRemotePath)) return "请填写目标远程路径。";
                break;
        }

        switch (t.ScheduleType)
        {
            case SyncScheduleType.Interval when t.IntervalMinutes < 1:
                return "固定间隔需 ≥ 1 分钟。";
            case SyncScheduleType.Weekly when t.WeekDays is not { Length: > 0 }:
                return "每周固定时刻：请至少选择一个周几。";
            case SyncScheduleType.Monthly when t.MonthDays is not { Length: > 0 }:
                return "每月固定：请至少选择一个日期。";
        }

        return null;
    }

    private async Task SaveToConfigAsync()
    {
        try
        {
            var config = await _config.LoadConfigAsync();
            config.SyncTasks = Tasks.ToArray();
            await _config.SaveConfigAsync(config);
            StatusMessage = "已保存（调度器将按计划执行；仅 App 运行期间生效）";
            Result("保存同步任务", true, StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = "保存失败: " + ex.Message;
            Result("保存同步任务", false, StatusMessage);
        }
    }

    private async Task RunNowAsync()
    {
        if (SelectedTask is null) { StatusMessage = "请先「新建」一个同步任务。"; Result("立即运行", false, StatusMessage); return; }
        var verr = ValidateTask(SelectedTask);
        if (verr is not null) { StatusMessage = verr; Result("立即运行", false, verr); return; }
        var task = SelectedTask;
        try
        {
            StatusMessage = "正在执行同步…";
            var config = await _config.LoadConfigAsync();
            var progress = new Progress<SyncProgress>(p => StatusMessage = p.Message);
            var result = await _sync.RunAsync(task, config, progress, CancellationToken.None);

            SyncStateStore.Save(task.Id, DateTime.Now, result.Success ? "ok" : "fail:" + result.Message);
            task.LastRunAt = DateTime.Now;
            task.LastResult = result.Success ? "ok" : "fail:" + result.Message;
            SyncLog.Write($"[手动] {task.Name}: {(result.Success ? "成功" : "失败")} - {result.Message}");

            StatusMessage = result.Success ? $"完成：{result.Message}" : $"失败：{result.Message}";
            RefreshTaskItem(task);
            Result("立即运行", result.Success, result.Message);
        }
        catch (Exception ex)
        {
            StatusMessage = "执行失败: " + ex.Message;
            Result("立即运行", false, StatusMessage);
        }
    }

    /// <summary>强制列表项刷新（LastResult/LastRunAt 无变更通知）。</summary>
    private void RefreshTaskItem(SyncTaskConfig task)
    {
        var idx = Tasks.IndexOf(task);
        if (idx < 0) return;
        Tasks.RemoveAt(idx);
        Tasks.Insert(idx, task);
        SelectedTask = task;
    }

    private void InvalidateTest()
    {
        if (_testedSignature is null) return;
        _testedSignature = null;
        OnPropertyChanged(nameof(TestPassed));
    }

    private static string SignatureOf(SyncTaskConfig t) =>
        string.Join('|',
            (int)t.Direction,
            t.LocalPath,
            t.SourceServerId,
            t.SourceRemotePath,
            t.TargetServerId,
            t.TargetRemotePath,
            t.DeleteExtra);

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
