using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LitSSHmcp.Agent.Scheduling;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.AiTasks;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>AI 定时任务管理窗口：查看/启停/立即运行/删除，并查看最近运行历史。</summary>
public partial class AiTasksWindow : FluentWindow
{
    private readonly AiTaskStore _store = AiTaskStore.Default;
    private readonly IUserDialogService _dialogs;
    private readonly ObservableCollection<AiTaskRow> _tasks = new();
    private readonly ObservableCollection<AiTaskRunRow> _runs = new();
    private readonly DispatcherTimer _refreshTimer;
    private bool _loading;

    public AiTasksWindow(IUserDialogService? dialogs = null)
    {
        InitializeComponent();
        _dialogs = dialogs ?? new UserDialogService();
        TaskGrid.ItemsSource = _tasks;
        RunGrid.ItemsSource = _runs;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        Loaded += (_, _) => AsyncOperation.Run("加载 AI 定时任务", ReloadAsync);
        Closed += (_, _) => _refreshTimer.Stop();
        _refreshTimer.Tick += (_, _) =>
        {
            if (IsVisible && !_loading)
                AsyncOperation.Run("刷新 AI 定时任务", ReloadAsync);
        };
        _refreshTimer.Start();
    }

    private async Task ReloadAsync()
    {
        if (_loading)
            return;
        _loading = true;
        try
        {
            var selectedId = (TaskGrid.SelectedItem as AiTaskRow)?.Task.Id;
            var tasks = await _store.ListAsync(includeDisabled: true);
            _tasks.Clear();
            foreach (var task in tasks)
                _tasks.Add(new AiTaskRow(task));

            if (selectedId is not null)
            {
                var row = _tasks.FirstOrDefault(t => string.Equals(t.Task.Id, selectedId, StringComparison.Ordinal));
                if (row is not null)
                    TaskGrid.SelectedItem = row;
            }

            StatusText.Text = $"共 {_tasks.Count} 个任务 · {DateTime.Now:HH:mm:ss}";
            await LoadRunsAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败：" + ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadRunsAsync()
    {
        _runs.Clear();
        if (TaskGrid.SelectedItem is not AiTaskRow row)
            return;
        try
        {
            var runs = await _store.ListRunsAsync(row.Task.Id, 20);
            foreach (var run in runs)
                _runs.Add(new AiTaskRunRow(run));
        }
        catch
        {
            // 运行历史读取失败不影响任务列表
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("刷新 AI 定时任务", ReloadAsync);

    private void OnTaskSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AsyncOperation.Run("加载 AI 任务运行历史", LoadRunsAsync);

    private void OnRunNowClick(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not AiTaskRow row)
        {
            StatusText.Text = "请先选择一个任务。";
            return;
        }
        AsyncOperation.Run("立即运行 AI 定时任务", async () =>
        {
            await _store.RequestRunAsync(row.Task.Id);
            StatusText.Text = $"已请求立即运行「{row.Task.Name}」，约 20 秒内执行。";
        });
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not AiTaskRow row)
        {
            StatusText.Text = "请先选择一个任务。";
            return;
        }
        AsyncOperation.Run("启停 AI 定时任务", async () =>
        {
            await _store.SetEnabledAsync(row.Task.Id, !row.Task.Enabled);
            StatusText.Text = $"已{(row.Task.Enabled ? "停用" : "启用")}「{row.Task.Name}」。";
            await ReloadAsync();
        });
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not AiTaskRow row)
        {
            StatusText.Text = "请先选择一个任务。";
            return;
        }
        if (!_dialogs.Confirm($"删除 AI 定时任务「{row.Task.Name}」及其运行历史？此操作不可恢复。",
                "删除 AI 定时任务"))
            return;
        AsyncOperation.Run("删除 AI 定时任务", async () =>
        {
            await _store.DeleteAsync(row.Task.Id);
            StatusText.Text = $"已删除「{row.Task.Name}」。";
            await ReloadAsync();
        });
    }

    private void OnInstallWindowsTaskClick(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not AiTaskRow row)
        {
            StatusText.Text = "请先选择一个任务。";
            return;
        }
        AsyncOperation.Run("安装 Windows 计划任务", async () =>
        {
            await WindowsAiTaskService.InstallAsync(row.Task);
            StatusText.Text = $"已安装 Windows 计划任务「{WindowsAiTaskService.TaskName(row.Task)}」。";
        });
    }

    private void OnRemoveWindowsTaskClick(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not AiTaskRow row)
        {
            StatusText.Text = "请先选择一个任务。";
            return;
        }
        AsyncOperation.Run("移除 Windows 计划任务", async () =>
        {
            await WindowsAiTaskService.RemoveAsync(row.Task);
            StatusText.Text = "已移除 Windows 计划任务。";
        });
    }

    private sealed class AiTaskRow
    {
        public AiTaskRow(AiTaskConfig task) => Task = task;

        public AiTaskConfig Task { get; }
        public string EnabledText => Task.Enabled ? "是" : "否";
        public string Name => Task.Name + (Task.ReadOnly ? string.Empty : "（可写）");
        public string Schedule => Task.DescribeSchedule();
        public string NextRun => Task.NextRunAt?.ToString("MM-dd HH:mm") ?? "-";
        public string LastRun => Task.LastRunAt?.ToString("MM-dd HH:mm") ?? "-";
        public string LastResult =>
            string.IsNullOrWhiteSpace(Task.LastResult) ? "-" : Task.LastResult!.Length <= 14
                ? Task.LastResult!
                : Task.LastResult![..14];
    }

    private sealed class AiTaskRunRow
    {
        public AiTaskRunRow(AiTaskRun run) => Run = run;

        public AiTaskRun Run { get; }
        public string StartedAt => Run.StartedAt.ToString("MM-dd HH:mm:ss");
        public string Status => Run.Status switch
        {
            AiTaskRunStatus.Succeeded => "成功",
            AiTaskRunStatus.Failed => "失败",
            AiTaskRunStatus.Cancelled => "取消",
            AiTaskRunStatus.Skipped => "跳过",
            _ => "运行中"
        };
        public string Trigger => Run.Trigger switch
        {
            AiTaskTrigger.Manual => "手动",
            AiTaskTrigger.CatchUp => "补跑",
            AiTaskTrigger.Headless => "无头",
            _ => "定时"
        };
        public string Duration => Run.CompletedAt is null
            ? "-"
            : $"{(Run.CompletedAt.Value - Run.StartedAt).TotalSeconds:F1}s";
        public string Tokens => (Run.InputTokens + Run.OutputTokens).ToString();
        public string Summary => string.IsNullOrWhiteSpace(Run.Error) ? Run.Summary ?? string.Empty : Run.Error!;
    }
}
