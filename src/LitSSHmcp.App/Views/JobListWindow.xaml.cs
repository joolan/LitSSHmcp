using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services.Notifications;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class JobListWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AgentViewModel _vm;
    private readonly ObservableCollection<JobRow> _jobs = new();
    private readonly HashSet<string> _notifiedTerminal = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _refreshing;
    private bool _firstRefresh = true;
    private bool _showingResult;
    private string? _resultJobId;
    private string? _loadedDetailJobId;
    private string? _loadedDetailSignature;
    private bool _suppressSelection;

    public JobListWindow(AgentViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        JobGrid.ItemsSource = _jobs;
        _timer.Tick += (_, _) =>
        {
            if (AutoRefresh.IsChecked == true)
                _ = RefreshAsync(quiet: true);
        };
        Loaded += (_, _) =>
        {
            _timer.Start();
            _ = RefreshAsync();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnJobSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        _showingResult = false;
        _resultJobId = null;
        _loadedDetailJobId = null;
        _loadedDetailSignature = null;
        if (JobGrid.SelectedItem is JobRow row)
            _ = LoadDetailSafeAsync(row.JobId);
    }

    private void OnResultClick(object sender, RoutedEventArgs e) => _ = ShowResultAsync();

    private void OnCancelClick(object sender, RoutedEventArgs e) => _ = CancelSelectedAsync();

    private async Task ShowResultAsync()
    {
        if (JobGrid.SelectedItem is not JobRow row) return;
        _showingResult = true;
        _resultJobId = row.JobId;
        StatusText.Text = $"正在加载 {row.JobId} 的结果…（详情自动刷新暂停）";
        var text = await _vm.InvokeJobToolAsync("job_result", new Dictionary<string, object?>
        {
            ["jobId"] = row.JobId
        });
        DetailBox.Text = Pretty(text);
        StatusText.Text = "已加载任务结果";
    }

    private async Task LoadDetailSafeAsync(string jobId)
    {
        try { await LoadDetailAsync(jobId); }
        catch { /* 详情加载失败不弹异常，自动刷新会重试 */ }
    }

    private async Task CancelSelectedAsync()
    {
        if (JobGrid.SelectedItem is not JobRow row) return;
        _showingResult = false;
        _resultJobId = null;
        CancelButton.IsEnabled = false;
        StatusText.Text = $"正在取消 {row.JobId}…";
        await _vm.InvokeJobToolAsync("job_cancel", new Dictionary<string, object?> { ["jobId"] = row.JobId });
        await RefreshAsync();
    }

    private async Task RefreshAsync(bool quiet = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var selected = (JobGrid.SelectedItem as JobRow)?.JobId;
            var text = await _vm.InvokeJobToolAsync("job_list", new Dictionary<string, object?>
            {
                ["limit"] = 50
            });

            if (!TryParseJobList(text, out var success, out var error, out var jobs))
            {
                if (!quiet)
                {
                    StatusText.Text = "任务列表加载失败";
                    DetailBox.Text = text;
                }
                return;
            }
            if (!success)
            {
                if (!quiet)
                {
                    StatusText.Text = error ?? "任务列表加载失败";
                    DetailBox.Text = error ?? text;
                }
                return;
            }

            SyncJobs(jobs);
            StatusText.Text = $"共 {_jobs.Count} 个任务";

            foreach (var job in _jobs.Where(IsTerminal))
            {
                if (_firstRefresh)
                {
                    _notifiedTerminal.Add(job.JobId);
                    continue;
                }
                if (_notifiedTerminal.Add(job.JobId))
                    NotifyCompleted(job);
            }
            _firstRefresh = false;

            var target = selected is null ? _jobs.FirstOrDefault() : _jobs.FirstOrDefault(j => j.JobId == selected);
            if (target is not null)
            {
                if (!ReferenceEquals(JobGrid.SelectedItem, target))
                {
                    _suppressSelection = true;
                    JobGrid.SelectedItem = target;
                    _suppressSelection = false;
                    _loadedDetailJobId = null;
                    _loadedDetailSignature = null;
                }

                var signature = $"{target.State}|{target.Progress:F0}|{target.ErrorStatus}";
                var shouldLoadDetail = !_showingResult ||
                                       !string.Equals(_resultJobId, target.JobId, StringComparison.Ordinal);
                if (shouldLoadDetail &&
                    (!string.Equals(_loadedDetailJobId, target.JobId, StringComparison.Ordinal) ||
                     !string.Equals(_loadedDetailSignature, signature, StringComparison.Ordinal)))
                {
                    await LoadDetailAsync(target.JobId);
                    _loadedDetailJobId = target.JobId;
                    _loadedDetailSignature = signature;
                }
            }
            else
            {
                _showingResult = false;
                _resultJobId = null;
                _loadedDetailJobId = null;
                _loadedDetailSignature = null;
                DetailBox.Text = "当前没有任务";
                CancelButton.IsEnabled = false;
                ResultButton.IsEnabled = false;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void SyncJobs(IReadOnlyList<JobRow> jobs)
    {
        var existing = _jobs.ToDictionary(j => j.JobId, StringComparer.Ordinal);
        var survivors = new List<JobRow>(jobs.Count);
        foreach (var job in jobs)
        {
            if (existing.Remove(job.JobId, out var row))
            {
                row.UpdateFrom(job);
                survivors.Add(row);
            }
            else
            {
                survivors.Add(job);
            }
        }

        foreach (var stale in existing.Values)
            _jobs.Remove(stale);

        for (var i = 0; i < survivors.Count; i++)
        {
            if (i >= _jobs.Count)
            {
                _jobs.Add(survivors[i]);
                continue;
            }
            if (ReferenceEquals(_jobs[i], survivors[i]))
                continue;

            var oldIndex = _jobs.IndexOf(survivors[i]);
            if (oldIndex >= 0)
                _jobs.Move(oldIndex, i);
            else
                _jobs.Insert(i, survivors[i]);
        }

        while (_jobs.Count > survivors.Count)
            _jobs.RemoveAt(_jobs.Count - 1);
    }

    private async Task LoadDetailAsync(string jobId)
    {
        var text = await _vm.InvokeJobToolAsync("job_status", new Dictionary<string, object?>
        {
            ["jobId"] = jobId,
            ["includeEvents"] = 20
        });
        DetailBox.Text = Pretty(text);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var stateNode) ? stateNode.GetString() : null;
            CancelButton.IsEnabled = state is "pending" or "running";
            ResultButton.IsEnabled = state is "succeeded" or "failed" or "cancelled" or "orphaned";
        }
        catch
        {
            CancelButton.IsEnabled = false;
            ResultButton.IsEnabled = false;
        }
    }

    private static bool IsTerminal(JobRow job) =>
        job.State is "succeeded" or "failed" or "cancelled" or "orphaned";

    private static void NotifyCompleted(JobRow job)
    {
        var severity = job.State switch
        {
            "succeeded" => NotificationSeverity.Success,
            "cancelled" => NotificationSeverity.Warning,
            _ => NotificationSeverity.Error
        };
        NotificationCenterService.Add("长任务", $"{job.Operation} {job.JobId}",
            $"任务 {job.State}：{job.Progress:F0}%{(job.ErrorStatus is null ? "" : $"（{job.ErrorStatus}）")}",
            severity);
    }

    private static string FormatLocalTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : value;
    }

    internal static string Pretty(string text)
    {
        // ToolResultFormatter 会递归展开嵌套 JSON 字符串（如事件 message）并用 relaxed encoder，
        // 因此旧任务里已经存成 \uXXXX 的 message 也能在展示时恢复为中文。
        return ToolResultFormatter.Format(text, pretty: true);
    }

    internal static bool TryParseJobList(string text, out bool success, out string? error, out List<JobRow> jobs)
    {
        success = false;
        error = null;
        jobs = new List<JobRow>();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(text);
            return TryParseJobListRoot(doc.RootElement, out success, out error, out jobs);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseJobListRoot(JsonElement root, out bool success, out string? error,
        out List<JobRow> jobs)
    {
        success = false;
        error = null;
        jobs = new List<JobRow>();

        // 兼容 MCP 返回被双重序列化成 JSON 字符串的情况。
        if (root.ValueKind == JsonValueKind.String)
            return TryParseJobList(root.GetString() ?? string.Empty, out success, out error, out jobs);
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        // 兼容 CallToolResult：优先读取 structuredContent，其次 content[].text。
        if (root.TryGetProperty("structuredContent", out var structured) &&
            structured.ValueKind == JsonValueKind.Object)
            return TryParseJobListRoot(structured, out success, out error, out jobs);
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    block.TryGetProperty("text", out var textNode) &&
                    textNode.ValueKind == JsonValueKind.String &&
                    TryParseJobList(textNode.GetString() ?? string.Empty, out success, out error, out jobs))
                    return true;
            }
        }

        if (!root.TryGetProperty("success", out var successNode) ||
            successNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;

        success = successNode.GetBoolean();
        if (root.TryGetProperty("error", out var errorNode) && errorNode.ValueKind == JsonValueKind.String)
            error = errorNode.GetString();

        if (root.TryGetProperty("jobs", out var jobsNode) && jobsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in jobsNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                jobs.Add(new JobRow
                {
                    JobId = ReadString(item, "jobId"),
                    Operation = ReadString(item, "operation"),
                    State = ReadString(item, "state"),
                    Progress = ReadDouble(item, "progress"),
                    CreatedAt = ReadString(item, "createdAt"),
                    ErrorStatus = ReadStringOrNull(item, "errorStatus")
                });
            }
        }
        return true;
    }

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static string? ReadStringOrNull(JsonElement item, string name) =>
        item.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static double ReadDouble(JsonElement item, string name) =>
        item.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number &&
        node.TryGetDouble(out var value)
            ? value
            : 0;

    public sealed class JobRow : System.ComponentModel.INotifyPropertyChanged
    {
        private string _jobId = string.Empty;
        private string _operation = string.Empty;
        private string _state = string.Empty;
        private double _progress;
        private string _createdAt = string.Empty;
        private string? _errorStatus;

        public string JobId { get => _jobId; set => Set(ref _jobId, value); }
        public string Operation { get => _operation; set => Set(ref _operation, value); }
        public string State { get => _state; set => Set(ref _state, value); }
        public double Progress
        {
            get => _progress;
            set
            {
                if (Set(ref _progress, value))
                    Raise(nameof(ProgressText));
            }
        }
        public string CreatedAt
        {
            get => _createdAt;
            set
            {
                if (Set(ref _createdAt, value))
                    Raise(nameof(CreatedAtText));
            }
        }
        public string? ErrorStatus { get => _errorStatus; set => Set(ref _errorStatus, value); }
        public string ProgressText => $"{Progress:F0}%";
        public string CreatedAtText => FormatLocalTime(CreatedAt);

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        internal void UpdateFrom(JobRow other)
        {
            Operation = other.Operation;
            State = other.State;
            Progress = other.Progress;
            CreatedAt = other.CreatedAt;
            ErrorStatus = other.ErrorStatus;
        }

        private bool Set<T>(ref T field, T value,
            [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            Raise(name);
            return true;
        }

        private void Raise(string? name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
