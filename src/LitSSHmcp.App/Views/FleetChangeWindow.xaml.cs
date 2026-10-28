using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class FleetChangeWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly Regex PlanIdPattern = new(@"##\s+[^\n]*?计划\s+(\S+)", RegexOptions.Compiled);
    private readonly AgentViewModel _vm;
    private readonly IUserDialogService _dialogs;
    private readonly ObservableCollection<FleetTargetOption> _targets = new();
    private readonly ObservableCollection<string> _targetStatus = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _refreshing;
    private bool _plansLoaded;

    public FleetChangeWindow(AgentViewModel vm, IUserDialogService? dialogs = null)
    {
        _vm = vm;
        _dialogs = dialogs ?? new UserDialogService();
        InitializeComponent();
        TargetList.ItemsSource = _targets;
        TargetStatusList.ItemsSource = _targetStatus;
        _timer.Tick += (_, _) => _ = RefreshPlanAsync();
        Loaded += (_, _) =>
        {
            if (_plansLoaded) return;
            _plansLoaded = true;
            _ = LoadPlansAsync();
        };
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string mode }) return;
        _ = RunAsync(mode);
    }

    private void OnLoadTargets(object sender, RoutedEventArgs e) => _ = LoadTargetsAsync();

    private void OnPlanSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PlanSelect.SelectedItem is not string text) return;
        var planId = text.Split(' ', 2)[0];
        PlanIdBox.Text = planId;
        _ = RefreshPlanAsync();
    }

    private void OnDeletePlan(object sender, RoutedEventArgs e) => _ = DeletePlanAsync();

    private async Task DeletePlanAsync()
    {
        var planId = PlanIdBox.Text.Trim();
        if (planId.Length == 0) return;
        if (!_dialogs.Confirm($"删除计划 {planId}？（不影响已执行目标）", "删除计划")) return;
        if (await _vm.DeleteFleetPlanAsync(planId))
        {
            PlanIdBox.Text = string.Empty;
            _targetStatus.Clear();
            OutputBox.Text = "计划已删除。";
            await LoadPlansAsync();
        }
        else
        {
            StatusText.Text = "计划不存在或删除失败。";
        }
    }

    private async Task LoadPlansAsync()
    {
        try
        {
            var plans = await _vm.ListFleetPlansAsync();
            var selectedId = PlanIdBox.Text.Trim();
            PlanSelect.Items.Clear();
            foreach (var plan in plans)
            {
                var display = $"{plan.Id} · {plan.Phase} · {plan.Targets.Count} 台";
                PlanSelect.Items.Add(display);
                if (string.Equals(plan.Id, selectedId, StringComparison.Ordinal))
                    PlanSelect.SelectedItem = display;
            }
        }
        catch
        {
            // 最近计划加载失败不影响手工重新生成
        }
    }

    private void OnClearTargets(object sender, RoutedEventArgs e)
    {
        foreach (var target in _targets) target.IsSelected = false;
        ServerIdsBox.Text = string.Empty;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _vm.CancelFleetChange();
        StatusText.Text = "已发送取消请求，等待当前目标结束…";
    }

    private async Task LoadTargetsAsync()
    {
        LoadTargetsButton.IsEnabled = false;
        try
        {
            StatusText.Text = "正在加载目标清单…";
            var json = await _vm.GetFleetTargetsAsync(TargetQueryBox.Text.Trim());
            var selected = _targets.Where(t => t.IsSelected).Select(t => t.Id)
                .ToHashSet(StringComparer.Ordinal);
            _targets.Clear();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("servers", out var servers) ||
                servers.ValueKind != JsonValueKind.Array)
            {
                StatusText.Text = json;
                return;
            }
            foreach (var server in servers.EnumerateArray())
            {
                var id = ReadString(server, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var option = new FleetTargetOption
                {
                    Id = id!,
                    Name = ReadString(server, "name") ?? id!,
                    Host = ReadString(server, "host") ?? string.Empty,
                    CanExec = ReadNullableBool(server, "canExec")
                };
                option.PropertyChanged += OnTargetSelectionChanged;
                option.IsSelected = selected.Contains(id!);
                _targets.Add(option);
            }
            StatusText.Text = $"已加载 {_targets.Count} 台目标。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载目标失败：{ex.Message}";
        }
        finally
        {
            LoadTargetsButton.IsEnabled = true;
        }
    }

    private void OnTargetSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FleetTargetOption.IsSelected)) return;
        ServerIdsBox.Text = string.Join(", ", _targets.Where(t => t.IsSelected).Select(t => t.Id));
    }

    private async Task RunAsync(string mode)
    {
        if (!Confirm(mode)) return;

        var planId = PlanIdBox.Text.Trim();
        if (mode != "plan" && planId.Length == 0)
        {
            StatusText.Text = "请先生成计划，或填写计划 ID。";
            return;
        }

        SetBusy(true);
        StatusText.Text = $"执行中（{mode}）…如需审批请在主窗口处理。";
        OutputBox.Text = "执行中…";
        if (planId.Length > 0) _timer.Start();
        try
        {
            var text = await _vm.InvokeFleetChangeAsync(
                mode,
                planId,
                ServerIdsBox.Text.Trim(),
                CommandBox.Text.Trim(),
                RollbackBox.Text.Trim(),
                ReadInt(ConcurrencyBox.Text, 4),
                ReadInt(MaxTargetsBox.Text, 20),
                ReadInt(TimeoutBox.Text, 60),
                mode is "canary" or "apply" or "rollback" or "retry_failed");
            OutputBox.Text = string.IsNullOrWhiteSpace(text) ? "(无输出)" : text;
            if (mode == "plan")
            {
                var match = PlanIdPattern.Match(text);
                if (match.Success)
                {
                    PlanIdBox.Text = match.Groups[1].Value;
                    _ = LoadPlansAsync();
                    _timer.Start();
                }
            }
            await RefreshPlanAsync();
            StatusText.Text = $"完成（{mode}）。";
        }
        catch (Exception ex)
        {
            OutputBox.Text = $"失败：{ex.Message}";
            StatusText.Text = "执行失败。";
        }
        finally
        {
            _timer.Stop();
            SetBusy(false);
        }
    }

    private async Task RefreshPlanAsync()
    {
        if (_refreshing) return;
        var planId = PlanIdBox.Text.Trim();
        if (planId.Length == 0) return;
        _refreshing = true;
        try
        {
            var plan = await _vm.GetFleetPlanAsync(planId);
            if (plan is null) return;
            _targetStatus.Clear();
            var done = plan.TargetResults.Count(t => t.Status is "ok" or "failed" or "cancelled");
            _targetStatus.Add($"阶段 {plan.Phase} · 进度 {done}/{plan.TargetResults.Count}" +
                              (plan.Running ? " · 运行中" : ""));
            foreach (var target in plan.TargetResults)
            {
                var name = string.IsNullOrWhiteSpace(target.ServerName) ? target.ServerId : target.ServerName;
                _targetStatus.Add($"{StatusLabel(target.Status)} {name} [{target.Phase}]" +
                                  (string.IsNullOrWhiteSpace(target.Error) ? "" : $"：{target.Error}"));
            }
            if (plan.Running) StatusText.Text = $"执行中（{plan.Phase}）…";
        }
        catch
        {
            // 计划可能尚未落盘，下一次轮询重试
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool Confirm(string mode)
    {
        var message = mode switch
        {
            "canary" => "金丝雀会在 1 台目标上执行变更命令，继续？",
            "apply" => "全量 apply 会对剩余目标执行变更，确认 canary 已通过？",
            "rollback" => "回滚会对已变更目标执行回滚命令，继续？",
            "retry_failed" => "仅重试失败目标，继续？",
            _ => null,
        };
        return message is null || _dialogs.Confirm(message, "批量变更确认");
    }

    private void SetBusy(bool busy)
    {
        PlanButton.IsEnabled = !busy;
        DryButton.IsEnabled = !busy;
        CanaryButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy;
        RetryButton.IsEnabled = !busy;
        RollbackButton.IsEnabled = !busy;
        StatusButton.IsEnabled = !busy;
        LoadTargetsButton.IsEnabled = !busy;
        ClearTargetsButton.IsEnabled = !busy;
        PlanSelect.IsEnabled = !busy;
        DeletePlanButton.IsEnabled = !busy;
        TargetList.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadNullableBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    private static string StatusLabel(string status) => status switch
    {
        "ok" => "OK",
        "failed" => "ERR",
        "running" => "…",
        "cancelled" => "CANCEL",
        _ => "PENDING"
    };

    private static int ReadInt(string text, int fallback) =>
        int.TryParse(text, out var value) ? value : fallback;

    public sealed class FleetTargetOption : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Host { get; init; } = string.Empty;
        public bool? CanExec { get; init; }
        public bool CanSelect => CanExec != false;
        public string Display => $"{Name} · {Host}（{Id}）" + (CanExec == false ? "（无 exec 权限）" : "");

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
