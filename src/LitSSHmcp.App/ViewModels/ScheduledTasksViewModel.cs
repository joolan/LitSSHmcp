using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Scheduling;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed partial class ScheduledTasksViewModel : INotifyPropertyChanged
{
    private readonly SshServerConfig _server;
    private readonly ISshService _ssh;
    private readonly IUserDialogService _dialogs;
    private readonly IAuditLogService? _audit;
    private readonly Func<string, bool, Task<bool>>? _sendToAgent;

    private ScheduledTasksProbeData? _data;
    private bool _busy;
    private bool _actionBusy;
    private bool _scriptLoading;
    private string _statusMessage = "正在读取计划任务…";
    private string _searchText = string.Empty;
    private bool _showPaused = true;
    private ScheduledTaskInfo? _selectedTask;
    private string _scriptMetaText = string.Empty;
    private string _scriptInterpreterText = string.Empty;
    private string _scriptPreview = string.Empty;
    private string _scriptSyntaxText = string.Empty;
    private string? _scriptContent;
    private byte[]? _scriptOriginalBytes;
    private Encoding _scriptEncoding = new UTF8Encoding(false);
    private bool _scriptIsBinary;
    private CancellationTokenSource? _scriptCts;

    public ScheduledTasksViewModel(
        SshServerConfig server,
        ISshService ssh,
        IUserDialogService dialogs,
        IAuditLogService? audit = null,
        Func<string, bool, Task<bool>>? sendToAgent = null)
    {
        _server = server;
        _ssh = ssh;
        _dialogs = dialogs;
        _audit = audit;
        _sendToAgent = sendToAgent;

        TasksView = CollectionViewSource.GetDefaultView(Tasks);
        TasksView.Filter = FilterTask;

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsBusy && !IsActionBusy);
        LoadElevatedCommand = new RelayCommand(_ => _ = LoadElevatedAsync(), _ => CanElevate && !IsBusy && !IsActionBusy);
        PauseResumeCommand = new RelayCommand(_ => _ = TogglePauseAsync(), _ => CanPauseResume);
        RunNowCommand = new RelayCommand(_ => _ = RunNowAsync(), _ => CanRunNow);
        ReloadScriptCommand = new RelayCommand(_ => _ = LoadSelectedScriptAsync(force: true), _ => HasSelectedScript);
    }

    public SshServerConfig Server => _server;
    public string Title => $"计划任务 - {_server.Name}";
    public string Subtitle => $"{_server.Host}:{_server.Port}";

    public ObservableCollection<ScheduledTaskInfo> Tasks { get; } = new();
    public ICollectionView TasksView { get; }

    public ICommand RefreshCommand { get; }
    public ICommand LoadElevatedCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand RunNowCommand { get; }
    public ICommand ReloadScriptCommand { get; }

    public event Action<string, string, string, bool>? ShowResultRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (_busy == value) return;
            _busy = value;
            OnPropertyChanged();
            RaiseCommandStates();
        }
    }

    public bool IsActionBusy
    {
        get => _actionBusy;
        private set
        {
            if (_actionBusy == value) return;
            _actionBusy = value;
            OnPropertyChanged();
            RaiseCommandStates();
        }
    }

    public bool IsScriptLoading
    {
        get => _scriptLoading;
        private set
        {
            if (_scriptLoading == value) return;
            _scriptLoading = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value) return;
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string ServerInfoText { get; private set; } = string.Empty;
    public string CronServiceText { get; private set; } = string.Empty;
    public string SudoText { get; private set; } = string.Empty;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            TasksView.Refresh();
        }
    }

    public bool ShowPaused
    {
        get => _showPaused;
        set
        {
            if (_showPaused == value) return;
            _showPaused = value;
            OnPropertyChanged();
            TasksView.Refresh();
        }
    }

    public ScheduledTaskInfo? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (ReferenceEquals(_selectedTask, value)) return;
            _selectedTask = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedTask));
            OnPropertyChanged(nameof(HasSelectedScript));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedStateText));
            OnPropertyChanged(nameof(SelectedScheduleText));
            OnPropertyChanged(nameof(SelectedDescription));
            OnPropertyChanged(nameof(SelectedCommand));
            OnPropertyChanged(nameof(SelectedOwner));
            OnPropertyChanged(nameof(SelectedSource));
            OnPropertyChanged(nameof(SelectedNextRun));
            OnPropertyChanged(nameof(SelectedLastRun));
            OnPropertyChanged(nameof(SelectedKindText));
            OnPropertyChanged(nameof(PauseResumeText));
            OnPropertyChanged(nameof(PauseResumeHint));
            RaiseCommandStates();
            _ = LoadSelectedScriptAsync();
        }
    }

    public bool HasSelectedTask => SelectedTask is not null;
    public bool HasSelectedScript => !string.IsNullOrWhiteSpace(SelectedTask?.ScriptPath);
    public string SelectedTitle => SelectedTask?.Description is { Length: > 0 } description
        ? description
        : SelectedTask?.ScriptPath ?? SelectedTask?.SourceDisplay ?? "请选择计划任务";
    public string SelectedStateText => SelectedTask?.StateText ?? string.Empty;
    public string SelectedScheduleText => SelectedTask?.ScheduleDescription ?? string.Empty;
    public string SelectedDescription => SelectedTask?.ScheduleText ?? string.Empty;
    public string SelectedCommand => SelectedTask?.Command ?? string.Empty;
    public string SelectedOwner => SelectedTask?.Owner ?? string.Empty;
    public string SelectedSource => SelectedTask?.SourceDisplay ?? string.Empty;
    public string SelectedNextRun => SelectedTask?.NextRunText ?? string.Empty;
    public string SelectedLastRun => SelectedTask?.LastRunText ?? string.Empty;
    public string SelectedKindText => SelectedTask?.KindText ?? string.Empty;

    public bool CanElevate => _server.SudoType != SudoType.None;
    public bool CanPauseResume => SelectedTask is not null && !IsActionBusy && !IsBusy;
    public bool CanRunNow => SelectedTask is not null && !IsActionBusy && !IsBusy;
    public string PauseResumeText
    {
        get
        {
            if (SelectedTask is null)
            {
                return "暂停";
            }

            if (SelectedTask.IsSystemd)
            {
                return SelectedTask.IsPaused ? "启用并启动" : "暂停（停止并禁用）";
            }

            return SelectedTask.IsPaused ? "启用任务" : "暂停任务";
        }
    }

    public string PauseResumeHint => SelectedTask?.Kind switch
    {
        ScheduledTaskKind.SystemdTimer => "systemd timer：暂停=停止并取消开机启用，启用=开机启用并立即启动",
        ScheduledTaskKind.CronDirectory => "目录任务：暂停=移除执行权限，启用=恢复执行权限",
        _ => "cron：暂停=在行首添加 #LITSSH_PAUSED# 标记，启用=移除标记"
    };

    public string ScriptMetaText
    {
        get => _scriptMetaText;
        private set
        {
            if (_scriptMetaText == value) return;
            _scriptMetaText = value;
            OnPropertyChanged();
        }
    }

    public string ScriptInterpreterText
    {
        get => _scriptInterpreterText;
        private set
        {
            if (_scriptInterpreterText == value) return;
            _scriptInterpreterText = value;
            OnPropertyChanged();
        }
    }

    public string ScriptPreview
    {
        get => _scriptPreview;
        private set
        {
            if (_scriptPreview == value) return;
            _scriptPreview = value;
            OnPropertyChanged();
        }
    }

    public string ScriptSyntaxText
    {
        get => _scriptSyntaxText;
        private set
        {
            if (_scriptSyntaxText == value) return;
            _scriptSyntaxText = value;
            OnPropertyChanged();
        }
    }

    public bool ScriptIsBinary => _scriptIsBinary;

    public string? ScriptContent => _scriptContent;

    public Encoding ScriptEncoding => _scriptEncoding;

    private bool FilterTask(object item)
    {
        if (item is not ScheduledTaskInfo task)
        {
            return false;
        }

        if (!ShowPaused && task.IsPaused)
        {
            return false;
        }

        var keyword = SearchText.Trim();
        if (keyword.Length == 0)
        {
            return true;
        }

        return Contains(task.ScheduleDescription, keyword) ||
               Contains(task.ScheduleText, keyword) ||
               Contains(task.Command, keyword) ||
               Contains(task.Owner, keyword) ||
               Contains(task.SourceDisplay, keyword) ||
               Contains(task.ScriptPath, keyword);
    }

    private static bool Contains(string? value, string keyword)
        => value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;

    private void RaiseCommandStates()
    {
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LoadElevatedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PauseResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RunNowCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ReloadScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
