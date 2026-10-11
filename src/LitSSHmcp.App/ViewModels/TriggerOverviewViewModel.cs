using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

/// <summary>触发事件总览 / 编排可视化：只读展示每个触发事件的调度、下次/上次运行与动作流水线。</summary>
public sealed class TriggerOverviewViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _config;

    public TriggerOverviewViewModel(IConfigService config) => _config = config;

    public ObservableCollection<TriggerOverviewItem> Triggers { get; } = new();

    private string _status = string.Empty;
    public string Status { get => _status; set => Set(ref _status, value); }

    public async Task LoadAsync()
    {
        Triggers.Clear();
        try
        {
            var cfg = await _config.LoadConfigAsync();
            var now = DateTime.Now;
            foreach (var trigger in cfg.Triggers ?? Array.Empty<TriggerConfig>())
                Triggers.Add(new TriggerOverviewItem(trigger, now));
            Status = Triggers.Count == 0 ? "暂无触发事件（可在「触发事件」窗口新建）" : $"共 {Triggers.Count} 个触发事件";
        }
        catch (Exception ex)
        {
            Status = "加载失败: " + ex.Message;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>总览中的单个触发事件。</summary>
public sealed class TriggerOverviewItem
{
    public TriggerOverviewItem(TriggerConfig t, DateTime now)
    {
        Name = string.IsNullOrWhiteSpace(t.Name) ? "(未命名)" : t.Name;
        EnabledText = t.Enabled ? "启用" : "停用";
        TargetText = t.TargetType == TriggerTarget.Remote ? "远程服务器" : "本机";
        ScheduleText = DescribeSchedule(t);

        var next = t.ScheduleEnabled
            ? SyncSchedule.ComputeNextRun(t.ScheduleType, t.IntervalMinutes, t.WeekDays, t.MonthDays, t.TimeOfDay, t.LastRunAt, now)
            : null;
        NextRunText = next is null ? "-" : next.Value.ToString("MM-dd HH:mm");
        LastRunText = t.LastRunAt is null ? "从未运行" : t.LastRunAt.Value.ToString("yyyy-MM-dd HH:mm");
        LastResultText = string.IsNullOrWhiteSpace(t.LastResult) ? "-" : t.LastResult!;
        SyncTriggerText = t.AllowEventTrigger ? "允许被同步任务触发" : "不参与同步触发";

        Steps = (t.Actions ?? Array.Empty<TriggerAction>())
            .Select((a, i) => new TriggerStepItem(i + 1, a))
            .ToList();
    }

    public string Name { get; }
    public string EnabledText { get; }
    public string TargetText { get; }
    public string ScheduleText { get; }
    public string NextRunText { get; }
    public string LastRunText { get; }
    public string LastResultText { get; }
    public string SyncTriggerText { get; }
    public IReadOnlyList<TriggerStepItem> Steps { get; }

    private static string DescribeSchedule(TriggerConfig t)
    {
        if (!t.ScheduleEnabled)
            return "未启用定时（手动 / 同步触发）";
        return t.ScheduleType switch
        {
            SyncScheduleType.Interval => $"每 {t.IntervalMinutes} 分钟",
            SyncScheduleType.Weekly => "每周 " +
                string.Join("", (t.WeekDays ?? Array.Empty<int>()).OrderBy(x => x).Select(d => "日一二三四五六"[(d % 7 + 7) % 7])) +
                " " + t.TimeOfDay,
            SyncScheduleType.Monthly => "每月 " +
                string.Join(",", (t.MonthDays ?? Array.Empty<int>()).OrderBy(x => x)) + " 日 " + t.TimeOfDay,
            _ => "定时"
        };
    }
}

/// <summary>总览中的一步动作。</summary>
public sealed class TriggerStepItem
{
    public TriggerStepItem(int order, TriggerAction action)
    {
        Order = order;
        Label = action.Type switch
        {
            TriggerActionType.GitCommitPush => "Git 提交/推送",
            TriggerActionType.Webhook => "Webhook 通知",
            TriggerActionType.Backup => "目录备份",
            TriggerActionType.DatabaseBackup => "数据库备份",
            _ => action.Type.ToString()
        };
        Detail = action.Summarize();
        ContinueOnError = action.ContinueOnError;
        ContinueText = action.ContinueOnError ? "失败继续" : "失败中断";
    }

    public int Order { get; }
    public string Label { get; }
    public string Detail { get; }
    public bool ContinueOnError { get; }
    public string ContinueText { get; }
}
