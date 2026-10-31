using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Snapshot.Collectors;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed class DockerManagerViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly IUserDialogService _dialogs;
    private readonly IAuditLogService? _audit;
    private bool _busy;
    private bool _actionBusy;
    private bool _sudoAttempted;
    private bool _sudoWorked;
    private DockerContainerItem? _selectedContainer;

    private const string ProbeCommand =
        "echo ==DOCKER==; docker info --format '{{json .}}' 2>&1 || true; " +
        "echo ==PS==; docker ps -a --format '{{json .}}' 2>&1 || true; " +
        "echo ==STATS==; docker stats --no-stream --format '{{json .}}' 2>&1 || true; true";

    public DockerManagerViewModel(SshServerConfig server, ISshService ssh,
        IUserDialogService? dialogs = null, IAuditLogService? audit = null)
    {
        Server = server;
        _ssh = ssh;
        _dialogs = dialogs ?? new UserDialogService();
        _audit = audit;
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        LoadLogsCommand = new RelayCommand(_ => _ = LoadLogsAsync(), _ => SelectedContainer is not null);
        LoadInspectCommand = new RelayCommand(_ => _ = LoadInspectAsync(), _ => SelectedContainer is not null);
        StartCommand = new RelayCommand(_ => _ = RunActionAsync("start", "启动"), _ => CanStart);
        StopCommand = new RelayCommand(_ => _ = RunActionAsync("stop", "停止"), _ => CanStop);
        RestartCommand = new RelayCommand(_ => _ = RunActionAsync("restart", "重启"), _ => CanRestart);
        PauseCommand = new RelayCommand(_ => _ = RunActionAsync("pause", "暂停"), _ => CanPause);
        UnpauseCommand = new RelayCommand(_ => _ = RunActionAsync("unpause", "恢复"), _ => CanUnpause);
        ContainersView = CollectionViewSource.GetDefaultView(Containers);
        ContainersView.Filter = FilterContainer;
        ContainersView.SortDescriptions.Add(new SortDescription(
            nameof(DockerContainerItem.IsRunning), ListSortDirection.Descending));
        ContainersView.SortDescriptions.Add(new SortDescription(
            nameof(DockerContainerItem.Name), ListSortDirection.Ascending));
    }

    public SshServerConfig Server { get; }
    public string Title => $"Docker 查看/管理 - {Server.Name}";
    public string Subtitle => $"{Server.Host}:{Server.Port}";

    public ObservableCollection<DockerContainerItem> Containers { get; } = new();
    public ICollectionView ContainersView { get; }

    public ICommand RefreshCommand { get; }
    public ICommand LoadLogsCommand { get; }
    public ICommand LoadInspectCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand UnpauseCommand { get; }

    private bool CanStart => !_actionBusy && SelectedContainer is { IsRunning: false, IsPaused: false };
    private bool CanStop => !_actionBusy && SelectedContainer is { IsActive: true };
    private bool CanRestart => !_actionBusy && SelectedContainer is not null;
    private bool CanPause => !_actionBusy && SelectedContainer is { IsRunning: true, IsPaused: false };
    private bool CanUnpause => !_actionBusy && SelectedContainer is { IsPaused: true };

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            ContainersView.Refresh();
        }
    }

    private bool _showStopped = true;
    public bool ShowStopped
    {
        get => _showStopped;
        set
        {
            if (_showStopped == value) return;
            _showStopped = value;
            OnPropertyChanged();
            ContainersView.Refresh();
        }
    }

    public DockerContainerItem? SelectedContainer
    {
        get => _selectedContainer;
        set
        {
            if (ReferenceEquals(_selectedContainer, value)) return;
            _selectedContainer = value;
            LogsText = string.Empty;
            InspectText = string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedContainer));
            OnPropertyChanged(nameof(SelectedContainerTitle));
            RaiseCommandStates();
        }
    }

    public bool HasSelectedContainer => SelectedContainer is not null;
    public string SelectedContainerTitle => SelectedContainer is null ? "请选择容器" : SelectedContainer.Name;

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; OnPropertyChanged(); } }

    private bool _isLoadingLogs;
    public bool IsLoadingLogs { get => _isLoadingLogs; private set { _isLoadingLogs = value; OnPropertyChanged(); } }

    private bool _isLoadingInspect;
    public bool IsLoadingInspect { get => _isLoadingInspect; private set { _isLoadingInspect = value; OnPropertyChanged(); } }

    private string _dockerInfoText = "正在读取 Docker 信息…";
    public string DockerInfoText { get => _dockerInfoText; private set { _dockerInfoText = value; OnPropertyChanged(); } }

    private string _statusMessage = "读取中…";
    public string StatusMessage { get => _statusMessage; private set { _statusMessage = value; OnPropertyChanged(); } }

    private string _logsText = string.Empty;
    public string LogsText { get => _logsText; private set { _logsText = value; OnPropertyChanged(); } }

    private string _inspectText = string.Empty;
    public string InspectText { get => _inspectText; private set { _inspectText = value; OnPropertyChanged(); } }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        _sudoAttempted = false;
        _sudoWorked = false;
        IsBusy = true;
        StatusMessage = "读取 Docker 信息…";
        var previous = SelectedContainer?.Id;
        try
        {
            var result = await RunDockerAsync(ProbeCommand);
            if (IsPermissionDenied(result))
            {
                Containers.Clear();
                ContainersView.Refresh();
                SelectedContainer = null;
                DockerInfoText = "无权限访问 Docker daemon";
                StatusMessage = "当前 SSH 用户无法访问 /var/run/docker.sock（不在 docker 组），且未配置可用的 sudo 提权。" +
                                " 请使用有 docker 权限的账号，或在服务器配置中设置 sudo/Su 提权。";
                return;
            }
            if (!result.Success)
            {
                Containers.Clear();
                ContainersView.Refresh();
                SelectedContainer = null;
                if (_sudoAttempted && !_sudoWorked)
                {
                    DockerInfoText = "无权限访问 Docker daemon";
                    StatusMessage = "直接访问无权限，sudo/Su 提权也失败: " +
                                    (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
                }
                else
                {
                    DockerInfoText = "Docker 不可用";
                    StatusMessage = "读取失败: " + (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
                }
                return;
            }

            var info = DockerSnapshotCollector.ParseDockerInfo(result.Output);
            var (containers, stats) = DockerSnapshotCollector.ParsePsAndStats(result.Output);
            var statsByName = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            var statsById = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            foreach (var stat in stats)
            {
                var name = NormalizeName(Get(stat, "name"));
                var id = Get(stat, "id");
                if (!string.IsNullOrWhiteSpace(name)) statsByName[name] = stat;
                if (!string.IsNullOrWhiteSpace(id)) statsById[id] = stat;
            }

            Containers.Clear();
            foreach (var raw in containers)
            {
                var name = NormalizeName(Get(raw, "name"));
                var id = Get(raw, "id");
                statsByName.TryGetValue(name, out var stat);
                if (stat is null && id.Length >= 12)
                    statsById.TryGetValue(id[..12], out stat);
                Containers.Add(DockerContainerItem.From(raw, stat));
            }
            ContainersView.Refresh();

            var running = Containers.Count(c => c.IsRunning);
            var version = info is null ? "Docker" : $"Docker {Get(info, "serverVersion")}";
            var images = info is null ? string.Empty : $" · {Get(info, "images")} 镜像";
            var viaSudo = _sudoAttempted && _sudoWorked ? " · sudo" : string.Empty;

            if (Containers.Count == 0)
            {
                SelectedContainer = null;
                DockerInfoText = info is null
                    ? "未检测到 Docker 或 daemon 不可用/无权限"
                    : $"{version} · 0 容器{images}{viaSudo}";
                StatusMessage = info is null
                    ? "未发现容器。请确认 Docker 已安装、daemon 正在运行，且当前 SSH 用户有 docker 权限。"
                    : "当前没有容器。";
                return;
            }

            DockerInfoText = $"{version} · {running}/{Containers.Count} 容器运行中{images}{viaSudo}";
            StatusMessage = $"更新于 {DateTime.Now:HH:mm:ss}";

            SelectedContainer = Containers.FirstOrDefault(c =>
                                   string.Equals(c.Id, previous, StringComparison.OrdinalIgnoreCase))
                               ?? Containers.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = "读取失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _busy = false;
        }
    }

    private async Task LoadLogsAsync()
    {
        if (SelectedContainer is not { } item || IsLoadingLogs)
            return;
        IsLoadingLogs = true;
        LogsText = "读取日志中…";
        try
        {
            var command = $"docker logs --tail 300 -- {ShellQuote.Single(item.Name)} 2>&1";
            var result = await RunDockerAsync(command);
            LogsText = IsPermissionDenied(result)
                ? "无权限访问 Docker daemon；请在服务器配置中启用 sudo/Su 提权。"
                : string.IsNullOrWhiteSpace(result.Output)
                    ? (result.Success ? "(无日志输出)" : "读取失败: " + result.Error)
                    : result.Output;
        }
        catch (Exception ex)
        {
            LogsText = "读取失败: " + ex.Message;
        }
        finally
        {
            IsLoadingLogs = false;
        }
    }

    private async Task LoadInspectAsync()
    {
        if (SelectedContainer is not { } item || IsLoadingInspect)
            return;
        IsLoadingInspect = true;
        InspectText = "读取详情中…";
        try
        {
            var command = $"docker inspect -- {ShellQuote.Single(item.Name)}";
            var result = await RunDockerAsync(command);
            InspectText = IsPermissionDenied(result)
                ? "无权限访问 Docker daemon；请在服务器配置中启用 sudo/Su 提权。"
                : string.IsNullOrWhiteSpace(result.Output)
                    ? (result.Success ? "(无详情输出)" : "读取失败: " + result.Error)
                    : result.Output;
        }
        catch (Exception ex)
        {
            InspectText = "读取失败: " + ex.Message;
        }
        finally
        {
            IsLoadingInspect = false;
        }
    }

    private async Task RunActionAsync(string action, string label)
    {
        if (SelectedContainer is not { } item || _actionBusy)
            return;

        var warning = action is "stop" or "restart" or "pause";
        if (!_dialogs.Confirm($"确认{label}容器「{item.Name}」？", "Docker 操作", warning))
            return;

        _actionBusy = true;
        IsBusy = true;
        StatusMessage = $"{label}中…";
        RaiseCommandStates();
        var succeeded = false;
        try
        {
            var command = $"docker {action} -- {ShellQuote.Single(item.Name)}";
            var result = await RunDockerAsync(command);
            if (IsPermissionDenied(result))
            {
                StatusMessage = "无权限访问 Docker daemon；请在服务器配置中启用 sudo/Su 提权。";
                return;
            }

            succeeded = result.Success;
            StatusMessage = succeeded
                ? $"{label}成功: {item.Name}"
                : $"{label}失败: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error)}";

            if (_audit is not null)
            {
                try
                {
                    await _audit.LogCommandAsync(new CommandAuditLog
                    {
                        ServerId = Server.Id,
                        ServerName = Server.Name,
                        Tool = $"docker_{action}",
                        Command = command,
                        Status = succeeded ? CommandStatus.Executed : CommandStatus.Failed,
                        Result = succeeded ? StatusMessage : result.Error,
                        ExitCode = result.ExitCode,
                        Category = AuditCategory.Exec
                    });
                }
                catch
                {
                    // 审计失败不改变操作结果
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"{label}失败: {ex.Message}";
        }
        finally
        {
            _actionBusy = false;
            IsBusy = false;
            RaiseCommandStates();
        }

        if (succeeded)
            await RefreshAsync();
    }

    private async Task<CommandResult> RunDockerAsync(string command, CancellationToken ct = default)
    {
        var direct = await _ssh.ExecuteCommandAsync(Server, command, ct, timeoutSeconds: 30);
        if (!IsPermissionDenied(direct) || Server.SudoType == SudoType.None)
            return direct;

        _sudoAttempted = true;
        var sudo = await _ssh.ExecuteWithSudoAsync(Server, command, ct);
        _sudoWorked = sudo.Success && !IsPermissionDenied(sudo);
        return sudo;
    }

    private static bool IsPermissionDenied(CommandResult result)
    {
        var text = (result.Error + "\n" + result.Output).ToLowerInvariant();
        return text.Contains("permission denied", StringComparison.Ordinal) &&
               (text.Contains("docker.sock", StringComparison.Ordinal) ||
                text.Contains("docker daemon", StringComparison.Ordinal));
    }

    private bool FilterContainer(object item)
    {
        if (item is not DockerContainerItem container)
            return false;
        if (!ShowStopped && !container.IsRunning)
            return false;
        var keyword = SearchText.Trim();
        return keyword.Length == 0 ||
               container.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               container.Image.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               container.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[]
                 {
                     LoadLogsCommand, LoadInspectCommand, StartCommand, StopCommand,
                     RestartCommand, PauseCommand, UnpauseCommand
                 })
            (command as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string NormalizeName(string? name) =>
        (name ?? string.Empty).Split(',')[0].Trim().TrimStart('/');

    private static string Get(Dictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DockerContainerItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Image { get; init; }
    public required string State { get; init; }
    public required string Status { get; init; }
    public required string Ports { get; init; }
    public required string CreatedAt { get; init; }
    public required string RunningFor { get; init; }
    public required string CpuText { get; init; }
    public required string MemText { get; init; }
    public required string NetText { get; init; }
    public required string BlockText { get; init; }
    public required string PidsText { get; init; }

    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);
    public bool IsPaused => string.Equals(State, "paused", StringComparison.OrdinalIgnoreCase);
    public bool IsActive => IsRunning || IsPaused;
    public string StateText => State.ToLowerInvariant() switch
    {
        "running" => "运行中",
        "exited" => "已停止",
        "created" => "已创建",
        "restarting" => "重启中",
        "paused" => "已暂停",
        "dead" => "异常",
        _ => string.IsNullOrWhiteSpace(State) ? "未知" : State
    };
    public string StateGlyph => IsRunning ? "●" : "○";

    public static DockerContainerItem From(
        Dictionary<string, object?> raw, Dictionary<string, object?>? stats) => new()
    {
        Id = Get(raw, "id"),
        Name = Normalize(Get(raw, "name")),
        Image = Get(raw, "image"),
        State = Get(raw, "state"),
        Status = Get(raw, "status"),
        Ports = Get(raw, "ports"),
        CreatedAt = Get(raw, "createdAt"),
        RunningFor = Get(raw, "runningFor"),
        CpuText = stats is null ? "—" : Get(stats, "cpuPercent"),
        MemText = stats is null ? "—" : Get(stats, "memUsage"),
        NetText = stats is null ? "—" : Get(stats, "netIO"),
        BlockText = stats is null ? "—" : Get(stats, "blockIO"),
        PidsText = stats is null ? "—" : Get(stats, "pids")
    };

    private static string Normalize(string? name) =>
        (name ?? string.Empty).Split(',')[0].Trim().TrimStart('/');

    private static string Get(Dictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
}
