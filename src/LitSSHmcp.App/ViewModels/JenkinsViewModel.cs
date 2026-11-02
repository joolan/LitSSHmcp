using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Jenkins;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.ViewModels;

/// <summary>任务树节点。文件夹在展开时按需加载，避免大型 Jenkins 一次拉全量。</summary>
public sealed class JenkinsJobNode : INotifyPropertyChanged
{
    private readonly Func<JenkinsJobNode, Task>? _loadChildren;
    private bool _isExpanded;
    private bool _isLoading;
    private bool _isLoaded;

    public JenkinsJobNode(JenkinsJob job, Func<JenkinsJobNode, Task>? loadChildren = null)
    {
        Job = job;
        _loadChildren = loadChildren;
    }

    public JenkinsJob Job { get; private set; }
    internal void UpdateJob(JenkinsJob job) => Job = job;
    public string Name => Job.Name;
    public string Path => Job.Path;
    public string Url => Job.Url;
    public bool IsFolder => Job.IsFolder;
    public bool Buildable => Job.Buildable;
    public ObservableCollection<JenkinsJobNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value))
                OnPropertyChanged(nameof(StateText));
        }
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set
        {
            if (Set(ref _isLoaded, value))
                OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText => IsLoading ? "加载中..." : IsFolder && !IsLoaded ? "展开加载" : string.Empty;

    public async Task EnsureChildrenAsync()
    {
        if (!IsFolder || IsLoaded || IsLoading || _loadChildren is null)
            return;

        IsLoading = true;
        try
        {
            await _loadChildren(this);
            IsLoaded = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record JenkinsJobRef(string Url, string Name, string Path)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Path : Name;
}

public sealed record JenkinsBuildCompletedInfo(
    string ConnectionName,
    string Environment,
    string JobName,
    string JobPath,
    int BuildNumber,
    string Result)
{
    public string ResultText => Result switch
    {
        "SUCCESS" => "成功",
        "FAILURE" => "失败",
        "ABORTED" => "已中止",
        "UNSTABLE" => "不稳定",
        _ => string.IsNullOrWhiteSpace(Result) ? "完成" : Result
    };
}

public sealed class JenkinsJobRow
{
    public JenkinsJobRow(JenkinsJobNode node, int depth, string statusKind)
    {
        Node = node;
        Depth = depth;
        StatusKind = statusKind;
    }

    public JenkinsJobNode Node { get; }
    public int Depth { get; }
    public string Name => Node.Name;
    public string Path => Node.Path;
    public bool IsFolder => Node.IsFolder;
    public string ExpandGlyph => IsFolder ? Node.IsExpanded ? "⌄" : "›" : string.Empty;
    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public string StatusKind { get; }
    public string StatusText => StatusKind switch
    {
        "queued" => "排队中",
        "building" => "构建中",
        "success" => "上次成功",
        "failure" => "上次失败",
        "aborted" => "上次中止",
        "unstable" => "上次不稳定",
        "disabled" => "已禁用",
        "folder" => "文件夹",
        _ => "从未构建"
    };
}

public sealed class JenkinsParameterField : INotifyPropertyChanged
{
    private string _value;

    public JenkinsParameterField(JenkinsParameterDefinition definition)
    {
        Name = definition.Name;
        Type = definition.Type;
        Description = definition.Description;
        Choices = definition.Choices;
        _value = definition.DefaultValue;
    }

    public string Name { get; }
    public string Type { get; }
    public string Description { get; }
    public IReadOnlyList<string> Choices { get; }
    public bool HasChoices => Choices.Count > 0;
    public bool IsSecret => Type.Contains("Password", StringComparison.OrdinalIgnoreCase);
    public bool IsBoolean => Type.Contains("Boolean", StringComparison.OrdinalIgnoreCase);
    public bool IsFile => Type.Contains("File", StringComparison.OrdinalIgnoreCase);
    public bool IsChoice => HasChoices && !IsBoolean && !IsSecret && !IsFile;
    public bool IsText => !IsSecret && !IsBoolean && !IsChoice && !IsFile;
    public string TypeLabel => Type switch
    {
        var value when value.Contains("Boolean", StringComparison.OrdinalIgnoreCase) => "布尔",
        var value when value.Contains("Choice", StringComparison.OrdinalIgnoreCase) => "选择",
        var value when value.Contains("Password", StringComparison.OrdinalIgnoreCase) => "密码",
        var value when value.Contains("File", StringComparison.OrdinalIgnoreCase) => "文件",
        var value when value.Contains("Text", StringComparison.OrdinalIgnoreCase) => "多行文本",
        _ => "字符串"
    };
    public string TypeHint => HasChoices
        ? $"{TypeLabel}；可选：{string.Join(" / ", Choices)}"
        : TypeLabel;

    public bool BoolValue
    {
        get => bool.TryParse(Value, out var parsed) && parsed;
        set => Value = value ? "true" : "false";
    }

    public string SelectedChoice
    {
        get => Value;
        set => Value = value ?? string.Empty;
    }

    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal))
                return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BoolValue)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChoice)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Jenkins 多连接页面。连接数量不设上限；测试/生产/第三方只是环境标签。
/// </summary>
public sealed class JenkinsViewModel : INotifyPropertyChanged, IConfigReloadable, IDisposable
{
    private const int MaxLogChars = 300_000;
    private readonly IConfigService _configService;
    private readonly IUserDialogService _dialogs;
    private readonly IWindowService _windows;
    private readonly IAuditLogService? _audit;
    private readonly Window? _owner;

    private JenkinsClient? _client;
    private SshServerConfig[] _servers = Array.Empty<SshServerConfig>();
    private JenkinsConnectionConfig? _selectedConnection;
    private JenkinsView? _selectedView;
    private JenkinsJobRow? _selectedRow;
    private HashSet<string> _queuedTaskUrls = new(StringComparer.OrdinalIgnoreCase);
    private JenkinsJobNode? _selectedJob;
    private JenkinsJobDetail? _selectedJobDetail;
    private JenkinsTestReport? _testReport;
    private JenkinsBuildSummary? _selectedBuild;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _jobCts;
    private CancellationTokenSource? _queueCts;
    private CancellationTokenSource? _buildWatchCts;
    private long _logOffset;
    private int? _logBuildNumber;
    private int _buildOffset;
    private bool _hasMoreBuilds;
    private bool _isLoadingMoreBuilds;
    private bool _isLogFollow = true;
    private readonly DispatcherTimer _queueTimer;
    private readonly HashSet<string> _notifiedBuilds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingBuildNotifications = new(StringComparer.OrdinalIgnoreCase);
    private bool _queueRefreshBusy;
    private bool _jobStatusRefreshBusy;
    private bool _isBusy;
    private bool _isJobLoading;
    private bool _isBuildLoading;
    private string _statusMessage = string.Empty;
    private string _errorMessage = string.Empty;
    private string _logText = string.Empty;
    private string _queueStatus = string.Empty;
    private bool _isQueueExpanded;
    private string _searchText = string.Empty;
    private int _sidebarTabIndex;
    private string? _recentPendingTopUrl;
    private long? _queueId;
    private bool _disposed;
    private int _connectionVersion;

    public JenkinsViewModel(
        IConfigService configService,
        Window? owner = null,
        IUserDialogService? dialogs = null,
        IWindowService? windows = null,
        IAuditLogService? audit = null)
    {
        _configService = configService;
        _owner = owner;
        _dialogs = dialogs ?? new UserDialogService();
        _windows = windows ?? new WpfWindowService();
        _audit = audit;
        _queueTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _queueTimer.Tick += (_, _) => _ = RefreshStatusesAsync();
        _queueTimer.Start();
        Builds.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BuildCount));

        RefreshCommand = new RelayCommand(_ => AsyncOperation.Run("刷新 Jenkins", RefreshAsync), _ => !_isBusy);
        AddConnectionCommand = new RelayCommand(_ => AddConnection(), _ => !_isBusy);
        EditConnectionCommand = new RelayCommand(_ => EditConnection(), _ => SelectedConnection is not null && !_isBusy);
        DeleteConnectionCommand = new RelayCommand(_ => AsyncOperation.Run("删除 Jenkins 连接", DeleteConnectionAsync),
            _ => SelectedConnection is not null && !_isBusy);
        BuildCommand = new RelayCommand(_ => AsyncOperation.Run("触发 Jenkins 构建", BuildAsync), _ => CanBuild);
        StopBuildCommand = new RelayCommand(_ => AsyncOperation.Run("停止 Jenkins 构建", StopBuildAsync), _ => CanStopBuild);
        CancelQueueCommand = new RelayCommand(_ => AsyncOperation.Run("取消 Jenkins 排队", CancelQueueAsync), _ => CanCancelQueue);
        RefreshBuildCommand = new RelayCommand(_ => AsyncOperation.Run("刷新 Jenkins 构建", RefreshBuildAsync), _ => SelectedBuild is not null);
        RefreshLogCommand = new RelayCommand(_ => AsyncOperation.Run("刷新 Jenkins 日志", RefreshLogAsync), _ => SelectedBuild is not null && !IsBusy);
        ToggleRowCommand = new RelayCommand(
            row => AsyncOperation.Run("展开 Jenkins 任务", () => ToggleRowAsync(row as JenkinsJobRow)),
            row => row is JenkinsJobRow { IsFolder: true });
        ToggleQueueCommand = new RelayCommand(_ => IsQueueExpanded = !IsQueueExpanded, _ => HasQueueItems);
        ToggleFavoriteCommand = new RelayCommand(_ => ToggleFavorite(), _ => SelectedJob is not null);
        LoadMoreBuildsCommand = new RelayCommand(
            _ => AsyncOperation.Run("加载更多 Jenkins 构建", LoadMoreBuildsAsync),
            _ => HasMoreBuilds && SelectedJob is not null && !IsLoadingMoreBuilds);
        OpenJobRefCommand = new RelayCommand(item => OpenJobRef(item as JenkinsJobRef), item => item is JenkinsJobRef);
        OpenArtifactCommand = new RelayCommand(item => OpenArtifact(item as JenkinsArtifact), item => item is JenkinsArtifact);
        RefreshQueueCommand = new RelayCommand(_ => AsyncOperation.Run("刷新 Jenkins 队列", RefreshQueueAsync), _ => SelectedConnection is not null && !IsBusy);
        CancelQueueItemCommand = new RelayCommand(
            item => AsyncOperation.Run("取消 Jenkins 排队", () => CancelQueueItemAsync(item as JenkinsQueueEntry)),
            item => item is JenkinsQueueEntry && SelectedConnection is { ReadOnly: false, Enabled: true });
        ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);

        _ = LoadConnectionsAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<JenkinsBuildCompletedInfo>? BuildCompleted;

    public ObservableCollection<JenkinsConnectionConfig> Connections { get; } = new();
    public ObservableCollection<JenkinsView> Views { get; } = new();
    public ObservableCollection<JenkinsJobNode> JobRoots { get; } = new();
    public ObservableCollection<JenkinsJobRow> JobRows { get; } = new();
    public ObservableCollection<JenkinsParameterField> Parameters { get; } = new();
    public ObservableCollection<JenkinsBuildSummary> Builds { get; } = new();
    public ObservableCollection<JenkinsJobRow> SearchResults { get; } = new();
    public ObservableCollection<JenkinsQueueEntry> QueueItems { get; } = new();
    public ObservableCollection<JenkinsJobRef> Favorites { get; } = new();
    public ObservableCollection<JenkinsJobRef> RecentJobs { get; } = new();
    public ObservableCollection<JenkinsJobRow> FilteredFavorites { get; } = new();
    public ObservableCollection<JenkinsJobRow> FilteredRecentJobs { get; } = new();
    public ObservableCollection<JenkinsArtifact> Artifacts { get; } = new();
    public ObservableCollection<JenkinsStage> PipelineStages { get; } = new();
    public bool HasFavorites => Favorites.Count > 0;
    public bool HasRecent => RecentJobs.Count > 0;
    public bool HasFilteredFavorites => FilteredFavorites.Count > 0;
    public bool HasFilteredRecent => FilteredRecentJobs.Count > 0;
    public bool HasArtifacts => Artifacts.Count > 0;
    public bool HasStages => PipelineStages.Count > 0;

    public ICommand RefreshCommand { get; }
    public ICommand AddConnectionCommand { get; }
    public ICommand EditConnectionCommand { get; }
    public ICommand DeleteConnectionCommand { get; }
    public ICommand BuildCommand { get; }
    public ICommand StopBuildCommand { get; }
    public ICommand CancelQueueCommand { get; }
    public ICommand RefreshBuildCommand { get; }
    public ICommand RefreshLogCommand { get; }
    public ICommand ToggleRowCommand { get; }
    public ICommand ToggleQueueCommand { get; }
    public ICommand RefreshQueueCommand { get; }
    public ICommand CancelQueueItemCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand LoadMoreBuildsCommand { get; }
    public ICommand OpenJobRefCommand { get; }
    public ICommand OpenArtifactCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public JenkinsConnectionConfig? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (ReferenceEquals(_selectedConnection, value))
                return;

            _selectedConnection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionSummary));
            OnPropertyChanged(nameof(IsReadOnlyConnection));
            OnPropertyChanged(nameof(HasSelectedConnection));
            OnPropertyChanged(nameof(HasTunnel));
            RaiseCommandStates();
            _ = SelectConnectionAsync(value);
        }
    }

    public bool HasSelectedConnection => SelectedConnection is not null;
    public bool HasTunnel => !string.IsNullOrWhiteSpace(SelectedConnection?.TunnelSshServerId);
    public string ConnectionSummary => SelectedConnection is null
        ? "未选择 Jenkins"
        : $"{SelectedConnection.DisplayName} · {SelectedConnection.EnvironmentDisplay}" +
          (SelectedConnection.ReadOnly ? " · 只读" : string.Empty) +
          (string.IsNullOrWhiteSpace(SelectedConnection.TunnelSshServerId) ? string.Empty : " · SSH 隧道");

    public bool IsReadOnlyConnection => SelectedConnection?.ReadOnly == true;

    public JenkinsView? SelectedView
    {
        get => _selectedView;
        set
        {
            if (ReferenceEquals(_selectedView, value))
                return;

            _selectedView = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedViewDescription));
            if (_client is not null)
                _ = LoadJobsAsync();
        }
    }

    public string SelectedViewDescription => SelectedView?.Description ?? string.Empty;

    public JenkinsTestReport? TestReport
    {
        get => _testReport;
        private set
        {
            if (ReferenceEquals(_testReport, value))
                return;
            _testReport = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTestReport));
        }
    }

    public bool HasTestReport => TestReport is not null;

    public JenkinsJobRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (ReferenceEquals(_selectedRow, value))
                return;
            _selectedRow = value;
            OnPropertyChanged();
            if (value is not null)
                SelectedJob = value.Node;
        }
    }

    public JenkinsJobNode? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (ReferenceEquals(_selectedJob, value))
                return;

            _selectedJob = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedJobTitle));
            OnPropertyChanged(nameof(SelectedJobPath));
            OnPropertyChanged(nameof(SelectedJobUrl));
            OnPropertyChanged(nameof(HasSelectedJob));
            OnPropertyChanged(nameof(SelectedJobTypeLabel));
            OnPropertyChanged(nameof(IsSelectedFavorite));
            OnPropertyChanged(nameof(FavoriteActionText));
            RaiseCommandStates();
            _ = LoadJobDetailsAsync(value);
        }
    }

    public string SelectedJobTitle => SelectedJob?.Name ?? "请选择任务";
    public string SelectedJobPath => SelectedJob?.Path ?? string.Empty;
    public string SelectedJobUrl => SelectedJob?.Url ?? string.Empty;
    public bool HasSelectedJob => SelectedJob is not null;
    public string SelectedJobTypeLabel
    {
        get
        {
            var className = SelectedJobDetail?.ClassName ?? SelectedJob?.Job.ClassName ?? string.Empty;
            if (SelectedJob?.IsFolder == true || className.Contains("Folder", StringComparison.OrdinalIgnoreCase))
                return "文件夹";
            if (className.Contains("MultiBranch", StringComparison.OrdinalIgnoreCase))
                return "多分支流水线";
            if (className.Contains("Workflow", StringComparison.OrdinalIgnoreCase))
                return "流水线";
            return "任务";
        }
    }
    public bool IsSelectedFavorite => SelectedJob is not null &&
        Favorites.Any(item => SameJobUrl(item.Url, SelectedJob.Url));
    public string FavoriteActionText => IsSelectedFavorite ? "取消收藏" : "收藏";
    public string SelectedJobDescription => SelectedJobDetail?.Description ?? string.Empty;
    public bool HasJobDescription => !string.IsNullOrWhiteSpace(SelectedJobDescription);
    public bool HasParameters => Parameters.Count > 0;
    public int BuildCount => Builds.Count;
    public bool HasBuilds => Builds.Count > 0;
    public bool HasMoreBuilds
    {
        get => _hasMoreBuilds;
        private set
        {
            if (!Set(ref _hasMoreBuilds, value))
                return;
            (LoadMoreBuildsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsLoadingMoreBuilds
    {
        get => _isLoadingMoreBuilds;
        private set
        {
            if (!Set(ref _isLoadingMoreBuilds, value))
                return;
            (LoadMoreBuildsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value))
                return;
            OnPropertyChanged(nameof(HasSearch));
            OnPropertyChanged(nameof(SearchSummary));
            RebuildSearchResults();
            RefreshRefFilters();
        }
    }

    public int SidebarTabIndex
    {
        get => _sidebarTabIndex;
        set
        {
            var previous = _sidebarTabIndex;
            if (!Set(ref _sidebarTabIndex, value))
                return;

            if (value == 1 && previous != 1)
                ApplyPendingRecentOrder();

            OnPropertyChanged(nameof(SearchSummary));
            OnPropertyChanged(nameof(SearchResultCount));
            OnPropertyChanged(nameof(HasSearchResults));
        }
    }

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasJobs => JobRoots.Count > 0;
    public int SearchResultCount => SidebarTabIndex switch
    {
        1 => FilteredRecentJobs.Count,
        2 => FilteredFavorites.Count,
        _ => SearchResults.Count
    };
    public bool HasSearchResults => SearchResultCount > 0;
    public string SearchSummary => !HasSearch
        ? string.Empty
        : SearchResultCount == 0
            ? "没有匹配的任务"
            : $"找到 {SearchResultCount} 个任务";

    public JenkinsJobDetail? SelectedJobDetail
    {
        get => _selectedJobDetail;
        private set
        {
            if (ReferenceEquals(_selectedJobDetail, value))
                return;
            _selectedJobDetail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedJobDetail));
        }
    }

    public bool HasSelectedJobDetail => SelectedJobDetail is not null;

    public JenkinsBuildSummary? SelectedBuild
    {
        get => _selectedBuild;
        set
        {
            if (ReferenceEquals(_selectedBuild, value))
                return;
            _selectedBuild = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedBuildStatusText));
            RaiseCommandStates();
            if (value is not null)
                _ = LoadBuildAsync(value.Number);
        }
    }

    public string QueueStatus
    {
        get => _queueStatus;
        private set
        {
            if (!Set(ref _queueStatus, value))
                return;
            OnPropertyChanged(nameof(HasQueueStatus));
            OnPropertyChanged(nameof(ShowQueuePanel));
            OnPropertyChanged(nameof(QueueSummary));
        }
    }

    public bool HasQueueStatus => QueueStatus.Length > 0;
    public bool HasQueueItems => QueueItems.Count > 0;
    public bool ShowQueuePanel => HasQueueItems || HasQueueStatus;
    public bool IsQueueExpanded
    {
        get => _isQueueExpanded;
        set
        {
            if (!Set(ref _isQueueExpanded, value))
                return;
            OnPropertyChanged(nameof(QueueSummary));
            OnPropertyChanged(nameof(QueueToggleText));
        }
    }

    public string QueueToggleText => IsQueueExpanded ? "收起队列" : "展开队列";
    public string QueueSummary => QueueItems.Count == 0
        ? QueueStatus
        : $"{QueueItems.Count} 个构建排队中";

    public string LogText
    {
        get => _logText;
        private set
        {
            if (!Set(ref _logText, value))
                return;
            OnPropertyChanged(nameof(HasLog));
        }
    }

    public bool HasLog => LogText.Length > 0;
    public string SelectedBuildStatusText => SelectedBuild is null
        ? string.Empty
        : $"{SelectedBuild.Display} · {SelectedBuild.TimestampText} · {SelectedBuild.DurationText}";

    public bool IsLogFollow
    {
        get => _isLogFollow;
        set => Set(ref _isLogFollow, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (!Set(ref _errorMessage, value))
                return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => ErrorMessage.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value))
                return;
            OnPropertyChanged(nameof(IsNotBusy));
            RaiseCommandStates();
        }
    }

    public bool IsNotBusy => !IsBusy;

    public bool IsJobLoading
    {
        get => _isJobLoading;
        private set => Set(ref _isJobLoading, value);
    }

    public bool IsBuildLoading
    {
        get => _isBuildLoading;
        private set => Set(ref _isBuildLoading, value);
    }

    public bool CanBuild => SelectedConnection is { ReadOnly: false, Enabled: true } &&
                            SelectedJob is { Buildable: true, IsFolder: false } &&
                            !IsBusy;

    public bool CanStopBuild => SelectedBuild is { Building: true } &&
                                SelectedConnection is { ReadOnly: false, Enabled: true } &&
                                !IsBusy;

    public bool CanCancelQueue => _queueId is not null &&
                                  SelectedConnection is { ReadOnly: false, Enabled: true } &&
                                  !IsBusy;

    public async Task RefreshAsync()
    {
        if (SelectedConnection is null)
        {
            await LoadConnectionsAsync();
            return;
        }

        await LoadViewsAsync();
        await LoadJobsAsync();
    }

    public void ReloadConfig()
    {
        _ = LoadConnectionsAsync();
    }

    public async Task LoadConnectionsAsync()
    {
        var previousId = SelectedConnection?.Id ?? UiPrefs.GetString("jenkins.lastConnectionId", string.Empty);
        try
        {
            var config = await _configService.LoadConfigAsync();
            _servers = config.Servers ?? Array.Empty<SshServerConfig>();
            var connections = (config.Jenkins?.Connections ?? Array.Empty<JenkinsConnectionConfig>())
                .Where(item => item is not null)
                .ToArray();

            Connections.Clear();
            foreach (var connection in connections)
                Connections.Add(connection);

            if (Connections.Count == 0)
            {
                SelectedConnection = null;
                StatusMessage = "还没有 Jenkins 连接，请先添加连接。";
                return;
            }

            var preferredId = !string.IsNullOrWhiteSpace(previousId)
                ? previousId
                : config.Jenkins?.DefaultConnectionId ?? string.Empty;
            var selected = Connections.FirstOrDefault(item => string.Equals(item.Id, preferredId, StringComparison.Ordinal))
                           ?? Connections.FirstOrDefault(item => item.Enabled)
                           ?? Connections[0];
            SelectedConnection = selected;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载 Jenkins 连接失败：{ex.Message}";
        }
    }

    private async Task SelectConnectionAsync(JenkinsConnectionConfig? connection)
    {
        var version = ++_connectionVersion;
        CancelPending();
        _client?.Dispose();
        _client = null;
        ClearJobState();

        if (connection is null)
        {
            StatusMessage = string.Empty;
            return;
        }

        UiPrefs.SetString("jenkins.lastConnectionId", connection.Id);
        if (!connection.Enabled)
        {
            StatusMessage = "该 Jenkins 连接已停用。";
            return;
        }

        JenkinsSshTunnel? tunnel = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(connection.TunnelSshServerId))
            {
                var sshServer = _servers.FirstOrDefault(server =>
                    string.Equals(server.Id, connection.TunnelSshServerId, StringComparison.Ordinal));
                if (sshServer is null)
                    throw new InvalidOperationException("找不到 Jenkins 连接指定的 SSH 隧道服务器。");
                if (sshServer.Disabled)
                    throw new InvalidOperationException($"SSH 隧道服务器已禁用：{sshServer.Name}");

                StatusMessage = $"正在通过 SSH 服务器建立隧道：{sshServer.Name}...";
                tunnel = await Task.Run(() => JenkinsSshTunnel.Start(
                    sshServer,
                    new FileSshKnownHostsStore(),
                    new SecurityOptionsProvider().SshHostKey.Mode));
                if (version != _connectionVersion)
                {
                    tunnel.Dispose();
                    return;
                }
            }

            if (version != _connectionVersion)
            {
                tunnel?.Dispose();
                return;
            }

            _client = new JenkinsClient(connection, tunnel: tunnel);
            tunnel = null;
            StatusMessage = $"已连接：{connection.DisplayName}";
            LoadSavedJobs(connection.Id);
            await LoadViewsAsync(preserveSelection: false);
            await LoadJobsAsync();
        }
        catch (Exception ex)
        {
            tunnel?.Dispose();
            ErrorMessage = $"创建 Jenkins 客户端失败：{ex.Message}";
        }
    }

    private async Task LoadViewsAsync(bool preserveSelection = true)
    {
        if (_client is null)
            return;

        var previousUrl = preserveSelection ? SelectedView?.Url : null;
        try
        {
            var views = await _client.ListViewsAsync();
            Views.Clear();
            Views.Add(new JenkinsView("全部任务", string.Empty, "当前连接的全部顶层任务"));
            foreach (var view in views)
                Views.Add(view);

            _selectedView = Views.FirstOrDefault(view => !string.IsNullOrWhiteSpace(previousUrl) &&
                                                         string.Equals(view.Url, previousUrl, StringComparison.Ordinal))
                           ?? Views[0];
            OnPropertyChanged(nameof(SelectedView));
            OnPropertyChanged(nameof(SelectedViewDescription));
            StatusMessage = views.Count == 0
                ? "当前连接没有配置 View，已显示全部任务。"
                : $"已加载 {views.Count} 个 View。";
        }
        catch (Exception ex)
        {
            Views.Clear();
            Views.Add(new JenkinsView("全部任务", string.Empty, "当前连接的全部顶层任务"));
            _selectedView = Views[0];
            OnPropertyChanged(nameof(SelectedView));
            OnPropertyChanged(nameof(SelectedViewDescription));
            StatusMessage = $"View 加载失败，已回退全部任务：{ex.Message}";
        }
    }

    private async Task ToggleRowAsync(JenkinsJobRow? row)
    {
        if (row is null || !row.IsFolder)
            return;

        row.Node.IsExpanded = !row.Node.IsExpanded;
        if (row.Node.IsExpanded)
            await row.Node.EnsureChildrenAsync();

        RebuildJobRows();
    }

    private JenkinsJobRow CreateJobRow(JenkinsJobNode node, int depth)
        => new(node, depth, GetJobStatus(node));

    private string GetJobStatus(JenkinsJobNode node)
    {
        if (node.IsFolder)
            return "folder";

        var color = node.Job.Color?.Trim().ToLowerInvariant() ?? string.Empty;
        if (color.Contains("_anime", StringComparison.Ordinal))
            return "building";
        if (_queuedTaskUrls.Contains(NormalizeJobUrl(node.Url)))
            return "queued";

        return color switch
        {
            "blue" => "success",
            "red" => "failure",
            "yellow" => "unstable",
            "aborted" => "aborted",
            "disabled" => "disabled",
            "grey" or "gray" or "notbuilt" => "idle",
            _ => "idle"
        };
    }

    private static string NormalizeJobUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim().TrimEnd(char.Parse("/"));

    private void RebuildJobRows()
    {
        var selectedNode = SelectedJob;
        JobRows.Clear();
        AddJobRows(JobRoots, 0);
        _selectedRow = JobRows.FirstOrDefault(row => ReferenceEquals(row.Node, selectedNode));
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(HasJobs));
    }

    private void AddJobRows(IEnumerable<JenkinsJobNode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            JobRows.Add(CreateJobRow(node, depth));
            if (node.IsExpanded && node.Children.Count > 0)
                AddJobRows(node.Children, depth + 1);
        }
    }

    private void RebuildSearchResults()
    {
        SearchResults.Clear();
        if (!HasSearch)
        {
            OnPropertyChanged(nameof(SearchResultCount));
            OnPropertyChanged(nameof(HasSearchResults));
            OnPropertyChanged(nameof(SearchSummary));
            return;
        }

        var keyword = SearchText.Trim();
        foreach (var node in FlattenJobs(JobRoots))
        {
            if (node.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                node.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                SearchResults.Add(CreateJobRow(node, 0));
                if (SearchResults.Count >= 200)
                    break;
            }
        }

        OnPropertyChanged(nameof(SearchResultCount));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchSummary));
    }

    private void RefreshRefFilters()
    {
        var keyword = SearchText.Trim();
        var selectedUrl = _selectedRow?.Node.Url;
        var selectedTab = SidebarTabIndex;
        FilteredFavorites.Clear();
        FilteredRecentJobs.Clear();
        foreach (var item in Favorites)
        {
            if (MatchesRef(item, keyword))
                FilteredFavorites.Add(CreateRefRow(item));
        }
        foreach (var item in RecentJobs)
        {
            if (MatchesRef(item, keyword))
                FilteredRecentJobs.Add(CreateRefRow(item));
        }

        if (selectedTab is 1 or 2 && !string.IsNullOrWhiteSpace(selectedUrl))
        {
            var replacement = (selectedTab == 1 ? FilteredRecentJobs : FilteredFavorites)
                .FirstOrDefault(row => SameJobUrl(row.Node.Url, selectedUrl));
            if (replacement is not null)
            {
                _selectedRow = replacement;
                OnPropertyChanged(nameof(SelectedRow));
            }
        }

        OnPropertyChanged(nameof(HasFilteredFavorites));
        OnPropertyChanged(nameof(HasFilteredRecent));
        OnPropertyChanged(nameof(SearchResultCount));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchSummary));
    }

    private static bool MatchesRef(JenkinsJobRef item, string keyword)
        => keyword.Length == 0 ||
           item.Display.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
           item.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private JenkinsJobRow CreateRefRow(JenkinsJobRef item)
    {
        var node = FindJobNode(JobRoots, item.Url);
        if (node is not null)
            return new JenkinsJobRow(node, 0, GetJobStatus(node));

        return new JenkinsJobRow(new JenkinsJobNode(new JenkinsJob
        {
            Name = item.Name,
            Path = item.Path,
            Url = item.Url,
            ClassName = "Job",
            Buildable = true
        }), 0, "idle");
    }

    private static JenkinsJobNode? FindJobNode(IEnumerable<JenkinsJobNode> nodes, string url)
    {
        foreach (var node in nodes)
        {
            if (SameJobUrl(node.Url, url))
                return node;

            var child = FindJobNode(node.Children, url);
            if (child is not null)
                return child;
        }

        return null;
    }

    private static IEnumerable<JenkinsJobNode> FlattenJobs(IEnumerable<JenkinsJobNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in FlattenJobs(node.Children))
                yield return child;
        }
    }

    private async Task LoadJobsAsync()
    {
        if (_client is null)
            return;

        CancelLoad();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        IsBusy = true;
        ErrorMessage = string.Empty;
        ClearJobState();
        try
        {
            var jobs = SelectedView is { IsAll: false } view
                ? await _client.ListJobsInViewAsync(view.Url, ct)
                : await _client.ListJobsAsync(ct: ct);
            foreach (var job in jobs)
                JobRoots.Add(CreateNode(job));
            OnPropertyChanged(nameof(HasJobs));
            RebuildJobRows();
            RebuildSearchResults();
            StatusMessage = $"已加载 {jobs.Count} 个任务。";
            await RefreshQueueAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private JenkinsJobNode CreateNode(JenkinsJob job)
        => new(job, async node => await LoadChildrenAsync(node));

    private async Task LoadChildrenAsync(JenkinsJobNode node)
    {
        if (_client is null)
            return;

        try
        {
            var children = await _client.ListJobsAsync(node.Url);
            node.Children.Clear();
            foreach (var child in children)
                node.Children.Add(CreateNode(child));
            RebuildJobRows();
            RebuildSearchResults();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
    }

    private async Task LoadJobDetailsAsync(JenkinsJobNode? node)
    {
        CancelJobLoad();
        CancelBuildWatch();
        SelectedJobDetail = null;
        Parameters.Clear();
        Builds.Clear();
        _buildOffset = 0;
        HasMoreBuilds = false;
        ClearBuildExtras();
        SelectedBuild = null;
        OnPropertyChanged(nameof(HasParameters));
        OnPropertyChanged(nameof(HasBuilds));
        OnPropertyChanged(nameof(SelectedJobTypeLabel));
        OnPropertyChanged(nameof(SelectedJobDescription));
        OnPropertyChanged(nameof(HasJobDescription));
        LogText = string.Empty;
        QueueStatus = string.Empty;
        _queueId = null;
        RaiseCommandStates();

        if (node is null || _client is null || node.IsFolder)
            return;

        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        IsJobLoading = true;
        try
        {
            var detail = await _client.GetJobAsync(node.Url, 0, 10, ct);
            if (ct.IsCancellationRequested)
                return;

            SelectedJobDetail = detail;
            RecordRecent(node);
            foreach (var parameter in detail.Parameters)
                Parameters.Add(new JenkinsParameterField(parameter));
            foreach (var build in detail.Builds)
                Builds.Add(build);
            _buildOffset = detail.Builds.Count;
            HasMoreBuilds = detail.Builds.Count >= 10;
            OnPropertyChanged(nameof(SelectedJobTypeLabel));
            OnPropertyChanged(nameof(SelectedJobDescription));
            OnPropertyChanged(nameof(HasJobDescription));
            OnPropertyChanged(nameof(HasParameters));
            OnPropertyChanged(nameof(HasBuilds));
            SelectedBuild = Builds.FirstOrDefault();
            StatusMessage = detail.Parameters.Count == 0
                ? "该任务无参数。"
                : $"已加载 {detail.Parameters.Count} 个参数。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsJobLoading = false;
        }
    }

    private async Task BuildAsync()
    {
        if (SelectedConnection is null || SelectedJob is null || _client is null)
            return;

        if (SelectedConnection.ReadOnly)
        {
            _dialogs.Warn("该连接为只读连接，不允许触发构建。", "Jenkins");
            return;
        }

        var parameters = Parameters.ToDictionary(item => item.Name, item => item.Value);
        if (SelectedConnection.RequireConfirmation)
        {
            var message = $"连接：{SelectedConnection.DisplayName}（{SelectedConnection.EnvironmentDisplay}）\n" +
                          $"任务：{SelectedJob.Path}\n" +
                          $"参数：{FormatParameters(Parameters)}\n\n确认触发构建？";
            if (!_dialogs.Confirm(message, "确认触发 Jenkins 构建"))
                return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        _queueId = null;
        QueueStatus = string.Empty;
        try
        {
            var result = await _client.TriggerBuildAsync(SelectedJob.Url, parameters);
            QueueStatus = result.QueueId is null
                ? "构建已触发。"
                : $"已进入队列（#{result.QueueId}）。";
            _queueId = result.QueueId;
            RaiseCommandStates();
            StatusMessage = "Jenkins 已接收构建请求。";
            await AuditAsync("jenkins_build_trigger",
                $"{SelectedJob.Path} params={FormatParameters(Parameters)} queue={result.QueueId}");
            await RefreshQueueAsync();
            if (result.QueueId is { } queueId)
                _ = PollQueueAsync(queueId);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PollQueueAsync(long queueId)
    {
        CancelQueuePolling();
        _queueCts = new CancellationTokenSource();
        var ct = _queueCts.Token;
        try
        {
            for (var attempt = 0; attempt < 150 && !ct.IsCancellationRequested; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (_client is null)
                    return;

                var item = await _client.GetQueueItemAsync(queueId, ct);
                QueueStatus = string.IsNullOrWhiteSpace(item.Why)
                    ? $"队列 #{queueId}"
                    : $"队列 #{queueId}：{item.Why}";

                if (item.Cancelled)
                {
                    QueueStatus = $"排队 #{queueId} 已取消。";
                    _queueId = null;
                    await RefreshQueueAsync();
                    return;
                }

                if (item.Executable is { } build)
                {
                    QueueStatus = $"已开始构建 #{build.Number}";
                    _queueId = null;
                    if (SelectedConnection is { } connection && SelectedJob is { } job)
                        _pendingBuildNotifications.Add($"{connection.Id}:{job.Path}:{build.Number}");
                    await RefreshQueueAsync();
                    await LoadBuildAsync(build.Number);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            RaiseCommandStates();
        }
    }

    private async Task LoadMoreBuildsAsync()
    {
        if (_client is null || SelectedJob is null || !HasMoreBuilds || IsLoadingMoreBuilds)
            return;

        var client = _client;
        var jobUrl = SelectedJob.Url;
        var offset = _buildOffset;
        IsLoadingMoreBuilds = true;
        StatusMessage = "正在加载更多构建记录...";
        try
        {
            var detail = await client.GetJobAsync(jobUrl, offset, 10);
            if (!ReferenceEquals(_client, client))
                return;

            var added = MergeBuilds(detail.Builds);
            if (added == 0 && offset > 0)
            {
                // 兼容部分 Jenkins/代理忽略 tree 范围参数的情况。
                var fallback = await client.GetJobAsync(jobUrl, 0, 100);
                if (!ReferenceEquals(_client, client))
                    return;
                added = MergeBuilds(fallback.Builds);
                _buildOffset = Builds.Count;
            }
            else
            {
                _buildOffset = offset + detail.Builds.Count;
            }

            HasMoreBuilds = added > 0 && _buildOffset > 0 && _buildOffset < 1000;
            OnPropertyChanged(nameof(BuildCount));
            OnPropertyChanged(nameof(HasBuilds));
            StatusMessage = added > 0 ? $"已加载更多构建记录（共 {Builds.Count} 条）。" : "没有更多构建记录了。";
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
            StatusMessage = $"加载更多失败：{ex.Message}";
        }
        finally
        {
            IsLoadingMoreBuilds = false;
        }
    }

    private int MergeBuilds(IReadOnlyList<JenkinsBuildSummary> source)
    {
        var added = 0;
        foreach (var build in source)
        {
            if (Builds.Any(item => item.Number == build.Number))
                continue;
            Builds.Add(build);
            added++;
        }

        return added;
    }

    private async Task LoadBuildAsync(int buildNumber)
    {
        if (_client is null || SelectedJob is null)
            return;

        CancelBuildWatch();
        var client = _client;
        var jobUrl = SelectedJob.Url;
        IsBuildLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var status = await client.GetBuildAsync(jobUrl, buildNumber);
            if (!ReferenceEquals(_client, client))
                return;

            UpdateBuildSummary(status);
            if (_logBuildNumber != buildNumber)
                ClearBuildExtras();
            await AppendLogAsync(client, jobUrl, buildNumber, reset: _logBuildNumber != buildNumber);
            await LoadBuildExtrasAsync(client, jobUrl, buildNumber);

            if (!status.Building && SelectedConnection is { } currentConnection && SelectedJob is { } currentJob)
            {
                var key = $"{currentConnection.Id}:{currentJob.Path}:{buildNumber}";
                if (_pendingBuildNotifications.Remove(key))
                    NotifyBuildCompleted(client, currentConnection.Id, currentConnection.DisplayName,
                        currentConnection.EnvironmentDisplay, currentJob.Name, currentJob.Path, buildNumber, status.Result);
            }
            if (status.Building)
                StartBuildWatch(client, jobUrl, buildNumber);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsBuildLoading = false;
        }
    }

    private async Task RefreshLogAsync()
    {
        if (_client is null || SelectedJob is null || SelectedBuild is null)
            return;

        await AppendLogAsync(_client, SelectedJob.Url, SelectedBuild.Number, reset: false);
    }

    private void StartBuildWatch(JenkinsClient client, string jobUrl, int buildNumber)
    {
        CancelBuildWatch();
        var connection = SelectedConnection;
        var job = SelectedJob;
        _buildWatchCts = new CancellationTokenSource();
        _ = WatchBuildAsync(
            client,
            jobUrl,
            buildNumber,
            connection?.Id ?? string.Empty,
            connection?.DisplayName ?? "Jenkins",
            connection?.EnvironmentDisplay ?? string.Empty,
            job?.Name ?? "构建",
            job?.Path ?? jobUrl,
            _buildWatchCts.Token);
    }

    private async Task WatchBuildAsync(
        JenkinsClient client,
        string jobUrl,
        int buildNumber,
        string connectionId,
        string connectionName,
        string environment,
        string jobName,
        string jobPath,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                var status = await client.GetBuildAsync(jobUrl, buildNumber, ct);
                if (!ReferenceEquals(_client, client))
                    return;

                UpdateBuildSummary(status);
                await AppendLogAsync(client, jobUrl, buildNumber, reset: false, ct);
                if (!status.Building)
                {
                    NotifyBuildCompleted(client, connectionId, connectionName, environment, jobName, jobPath, buildNumber, status.Result);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_client, client))
                ErrorMessage = Describe(ex);
        }
    }

    private void NotifyBuildCompleted(
        JenkinsClient client,
        string connectionId,
        string connectionName,
        string environment,
        string jobName,
        string jobPath,
        int buildNumber,
        string result)
    {
        if (!ReferenceEquals(_client, client))
            return;

        var key = $"{connectionId}:{jobPath}:{buildNumber}";
        _pendingBuildNotifications.Remove(key);
        if (!_notifiedBuilds.Add(key))
            return;

        BuildCompleted?.Invoke(new JenkinsBuildCompletedInfo(
            connectionName, environment, jobName, jobPath, buildNumber, result));
    }

    private async Task LoadBuildExtrasAsync(JenkinsClient client, string jobUrl, int buildNumber)
    {
        try
        {
            var extras = await client.GetBuildExtrasAsync(jobUrl, buildNumber);
            if (!ReferenceEquals(_client, client))
                return;

            Artifacts.Clear();
            foreach (var artifact in extras.Artifacts)
                Artifacts.Add(artifact);
            PipelineStages.Clear();
            foreach (var stage in extras.Stages)
                PipelineStages.Add(stage);
            TestReport = extras.TestReport;
            OnPropertyChanged(nameof(HasArtifacts));
            OnPropertyChanged(nameof(HasStages));
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_client, client))
                ErrorMessage = Describe(ex);
        }
    }

    private void ClearBuildExtras()
    {
        Artifacts.Clear();
        PipelineStages.Clear();
        TestReport = null;
        OnPropertyChanged(nameof(HasArtifacts));
        OnPropertyChanged(nameof(HasStages));
    }

    private async Task AppendLogAsync(
        JenkinsClient client,
        string jobUrl,
        int buildNumber,
        bool reset,
        CancellationToken ct = default)
    {
        if (reset || _logBuildNumber != buildNumber)
        {
            _logBuildNumber = buildNumber;
            _logOffset = 0;
            LogText = string.Empty;
        }

        var chunk = await client.GetBuildLogChunkAsync(jobUrl, buildNumber, _logOffset, ct);
        if (!ReferenceEquals(_client, client))
            return;

        if (chunk.Text.Length > 0)
        {
            var text = LogText + chunk.Text;
            LogText = text.Length <= MaxLogChars ? text : text[^MaxLogChars..];
        }

        _logOffset = Math.Max(_logOffset, chunk.NextStart);
    }

    private void UpdateBuildSummary(JenkinsBuildStatus status)
    {
        var summary = new JenkinsBuildSummary(
            status.Number,
            status.Url,
            status.Result,
            status.Building,
            status.Timestamp,
            status.Duration);
        var index = Builds.ToList().FindIndex(item => item.Number == status.Number);
        if (index >= 0)
            Builds[index] = summary;
        else
            Builds.Insert(0, summary);

        _selectedBuild = summary;
        OnPropertyChanged(nameof(SelectedBuild));
        OnPropertyChanged(nameof(SelectedBuildStatusText));
        RaiseCommandStates();
    }

    private async Task RefreshBuildAsync()
    {
        if (SelectedBuild is not null)
            await LoadBuildAsync(SelectedBuild.Number);
    }

    private async Task StopBuildAsync()
    {
        if (SelectedConnection is null || SelectedJob is null || SelectedBuild is null || _client is null)
            return;

        if (!_dialogs.Confirm(
                $"确认停止构建 #{SelectedBuild.Number}？",
                $"停止 {SelectedConnection.DisplayName}"))
            return;

        try
        {
            await _client.StopBuildAsync(SelectedJob.Url, SelectedBuild.Number);
            StatusMessage = $"已请求停止构建 #{SelectedBuild.Number}。";
            await AuditAsync("jenkins_build_stop", $"{SelectedJob.Path} #{SelectedBuild.Number}");
            await RefreshBuildAsync();
        }
        catch (JenkinsApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            // 部分 Jenkins/反向代理会先执行 stop，再返回 403；以构建实际状态为准。
            try
            {
                var status = await _client.GetBuildAsync(SelectedJob.Url, SelectedBuild.Number);
                if (!status.Building || string.Equals(status.Result, "ABORTED", StringComparison.OrdinalIgnoreCase))
                {
                    ErrorMessage = string.Empty;
                    StatusMessage = $"已停止构建 #{SelectedBuild.Number}。";
                    await AuditAsync("jenkins_build_stop", $"{SelectedJob.Path} #{SelectedBuild.Number}");
                    await LoadBuildAsync(SelectedBuild.Number);
                    return;
                }
            }
            catch
            {
                // 忽略状态复核失败，保留原始错误
            }

            ErrorMessage = Describe(ex);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
    }

    private async Task CancelQueueAsync()
    {
        if (_client is null || _queueId is not { } queueId)
            return;

        if (!_dialogs.Confirm($"确认取消排队 #{queueId}？", "取消 Jenkins 排队"))
            return;

        try
        {
            await _client.CancelQueueItemAsync(queueId);
            _queueId = null;
            QueueStatus = $"排队 #{queueId} 已取消。";
            await AuditAsync("jenkins_queue_cancel", $"queue={queueId}");
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
    }

    private void AddConnection()
    {
        var window = _windows.CreateJenkinsConnectionWindow(_owner);
        var vm = new JenkinsConnectionEditViewModel(_configService, dialogs: _dialogs);
        window.DataContext = vm;
        vm.DialogClosed += (_, ok) =>
        {
            window.Close();
            if (ok)
                _ = LoadConnectionsAsync();
        };
        window.ShowDialog();
    }

    private void EditConnection()
    {
        if (SelectedConnection is null)
            return;

        var window = _windows.CreateJenkinsConnectionWindow(_owner);
        var vm = new JenkinsConnectionEditViewModel(_configService, SelectedConnection, _dialogs);
        window.DataContext = vm;
        vm.DialogClosed += (_, ok) =>
        {
            window.Close();
            if (ok)
                _ = LoadConnectionsAsync();
        };
        window.ShowDialog();
    }

    private async Task DeleteConnectionAsync()
    {
        if (SelectedConnection is null)
            return;

        if (!_dialogs.Confirm(
                $"确认删除 Jenkins 连接 \"{SelectedConnection.DisplayName}\"？",
                "删除 Jenkins 连接"))
            return;

        try
        {
            var config = await _configService.LoadConfigAsync();
            if (config.Jenkins is null)
                return;
            config.Jenkins.Connections = (config.Jenkins.Connections ?? Array.Empty<JenkinsConnectionConfig>())
                .Where(item => !string.Equals(item.Id, SelectedConnection.Id, StringComparison.Ordinal))
                .ToArray();
            if (string.Equals(config.Jenkins.DefaultConnectionId, SelectedConnection.Id, StringComparison.Ordinal))
                config.Jenkins.DefaultConnectionId = config.Jenkins.Connections.FirstOrDefault()?.Id ?? string.Empty;
            await _configService.SaveConfigAsync(config);
            StatusMessage = "已删除 Jenkins 连接。";
            SelectedConnection = null;
            await LoadConnectionsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
    }

    private async Task RefreshStatusesAsync()
    {
        await RefreshQueueAsync();
        await RefreshJobStatusesAsync();
    }

    public async Task RefreshQueueAsync()
    {
        if (_client is null || _queueRefreshBusy)
            return;

        _queueRefreshBusy = true;
        try
        {
            var items = await _client.ListQueueItemsAsync();
            QueueItems.Clear();
            foreach (var item in items)
                QueueItems.Add(item);

            _queuedTaskUrls = new HashSet<string>(
                QueueItems.Select(item => NormalizeJobUrl(item.TaskUrl)),
                StringComparer.OrdinalIgnoreCase);
            RebuildJobRows();
            if (HasSearch)
                RebuildSearchResults();
            RefreshRefFilters();

            if (QueueItems.Count == 0)
            {
                IsQueueExpanded = false;
                _queueId = null;
                QueueStatus = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(QueueStatus))
            {
                QueueStatus = $"{QueueItems.Count} 个构建排队中";
            }

            NotifyQueueItemsChanged();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            _queueRefreshBusy = false;
        }
    }

    private async Task RefreshJobStatusesAsync()
    {
        if (_client is null || _jobStatusRefreshBusy || IsBusy)
            return;

        _jobStatusRefreshBusy = true;
        var client = _client;
        try
        {
            var jobs = SelectedView is { IsAll: false } view
                ? await client.ListJobsInViewAsync(view.Url)
                : await client.ListJobsAsync();
            if (!ReferenceEquals(_client, client))
                return;

            UpdateJobs(JobRoots, jobs);
            await RefreshLoadedChildrenAsync(client, JobRoots);
            RebuildJobRows();
            if (HasSearch)
                RebuildSearchResults();
            RefreshRefFilters();
        }
        catch
        {
            // 5 秒轮询失败不覆盖页面错误；用户仍可手动刷新。
        }
        finally
        {
            _jobStatusRefreshBusy = false;
        }
    }

    private async Task RefreshLoadedChildrenAsync(JenkinsClient client, IEnumerable<JenkinsJobNode> nodes)
    {
        foreach (var node in nodes.Where(item => item.IsFolder && item.IsLoaded).ToArray())
        {
            var children = await client.ListJobsAsync(node.Url);
            if (!ReferenceEquals(_client, client))
                return;

            UpdateJobs(node.Children, children);
            await RefreshLoadedChildrenAsync(client, node.Children);
        }
    }

    private void UpdateJobs(ObservableCollection<JenkinsJobNode> nodes, IReadOnlyList<JenkinsJob> jobs)
    {
        var existing = nodes
            .GroupBy(node => NormalizeJobUrl(node.Url), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var job in jobs)
        {
            var key = NormalizeJobUrl(job.Url);
            if (existing.TryGetValue(key, out var node))
                node.UpdateJob(job);
            else
                nodes.Add(CreateNode(job));
        }
    }

    private void NotifyQueueItemsChanged()
    {
        OnPropertyChanged(nameof(HasQueueItems));
        OnPropertyChanged(nameof(ShowQueuePanel));
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(HasQueueStatus));
        RaiseCommandStates();
    }

    private async Task CancelQueueItemAsync(JenkinsQueueEntry? item)
    {
        if (item is null || _client is null)
            return;

        if (!_dialogs.Confirm($"确认取消排队 #{item.Id}（{item.Display}）？", "取消 Jenkins 排队"))
            return;

        try
        {
            await _client.CancelQueueItemAsync(item.Id);
            if (_queueId == item.Id)
                _queueId = null;
            QueueStatus = $"排队 #{item.Id} 已取消。";
            await AuditAsync("jenkins_queue_cancel", $"{item.Display} queue={item.Id}");
            await RefreshQueueAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
    }

    private void ClearJobState()
    {
        JobRoots.Clear();
        JobRows.Clear();
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasJobs));
        _selectedRow = null;
        OnPropertyChanged(nameof(SelectedRow));
        SelectedJob = null;
        SelectedJobDetail = null;
        Parameters.Clear();
        Builds.Clear();
        _buildOffset = 0;
        HasMoreBuilds = false;
        ClearBuildExtras();
        SelectedBuild = null;
        OnPropertyChanged(nameof(HasParameters));
        OnPropertyChanged(nameof(HasBuilds));
        OnPropertyChanged(nameof(SelectedJobTypeLabel));
        OnPropertyChanged(nameof(SelectedJobDescription));
        OnPropertyChanged(nameof(HasJobDescription));
        LogText = string.Empty;
        QueueStatus = string.Empty;
        QueueItems.Clear();
        IsQueueExpanded = false;
        NotifyQueueItemsChanged();
        _queueId = null;
        _pendingBuildNotifications.Clear();
        _logBuildNumber = null;
        _logOffset = 0;
        CancelBuildWatch();
    }

    private void CancelPending()
    {
        CancelLoad();
        CancelJobLoad();
        CancelQueuePolling();
        CancelBuildWatch();
    }

    private void CancelLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private void CancelJobLoad()
    {
        _jobCts?.Cancel();
        _jobCts?.Dispose();
        _jobCts = null;
    }

    private void CancelQueuePolling()
    {
        _queueCts?.Cancel();
        _queueCts?.Dispose();
        _queueCts = null;
    }

    private void CancelBuildWatch()
    {
        _buildWatchCts?.Cancel();
        _buildWatchCts?.Dispose();
        _buildWatchCts = null;
    }

    private void RaiseCommandStates()
    {
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (EditConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BuildCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopBuildCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshBuildCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshLogCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelQueueItemCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleFavoriteCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string FormatParameters(IReadOnlyList<JenkinsParameterField> parameters)
    {
        if (parameters.Count == 0)
            return "无";

        return string.Join("；", parameters.Select(item =>
            $"{item.Name}={(item.IsSecret ? "******" : string.IsNullOrEmpty(item.Value) ? "(空)" : item.Value)}"));
    }

    private async Task AuditAsync(string action, string result)
    {
        if (_audit is null || SelectedConnection is null)
            return;

        try
        {
            await _audit.InitializeAsync();
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = $"jenkins:{SelectedConnection.Id}",
                ServerName = SelectedConnection.DisplayName,
                Command = action,
                Result = result,
                Status = CommandStatus.Executed,
                Category = AuditCategory.Exec,
                Tool = "jenkins",
                UserId = AuditContext.CurrentUserId
            });
        }
        catch
        {
            // 审计失败不阻断 Jenkins 操作
        }
    }

    private void RecordRecent(JenkinsJobNode node)
    {
        var item = new JenkinsJobRef(node.Url, node.Name, node.Path);
        var existing = RecentJobs.FirstOrDefault(recent => SameJobUrl(recent.Url, item.Url));
        if (existing is not null)
        {
            _recentPendingTopUrl = item.Url;
            return;
        }

        RecentJobs.Insert(0, item);
        while (RecentJobs.Count > 12)
            RecentJobs.RemoveAt(RecentJobs.Count - 1);
        if (SelectedConnection is { } connection)
            SaveJobRefs(connection.Id, "recent", RecentJobs);
        OnPropertyChanged(nameof(HasRecent));
        RefreshRefFilters();
    }

    private void ToggleFavorite()
    {
        if (SelectedJob is null)
            return;

        var existing = Favorites.FirstOrDefault(item => SameJobUrl(item.Url, SelectedJob.Url));
        if (existing is null)
            Favorites.Insert(0, new JenkinsJobRef(SelectedJob.Url, SelectedJob.Name, SelectedJob.Path));
        else
            Favorites.Remove(existing);

        if (SelectedConnection is { } connection)
            SaveJobRefs(connection.Id, "favorites", Favorites);
        OnPropertyChanged(nameof(HasFavorites));
        RefreshRefFilters();
        OnPropertyChanged(nameof(IsSelectedFavorite));
        OnPropertyChanged(nameof(FavoriteActionText));
    }

    private static void OpenArtifact(JenkinsArtifact? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Url))
            return;

        try
        {
            NativeShell.OpenFile(item.Url);
        }
        catch
        {
            // 外部浏览器打开失败不影响页面
        }
    }

    private void OpenJobRef(JenkinsJobRef? item)
    {
        if (item is null)
            return;

        SelectedJob = new JenkinsJobNode(new JenkinsJob
        {
            Name = item.Name,
            Path = item.Path,
            Url = item.Url,
            ClassName = "Job",
            Buildable = true
        });
    }

    private void ApplyPendingRecentOrder()
    {
        if (string.IsNullOrWhiteSpace(_recentPendingTopUrl))
            return;

        var item = RecentJobs.FirstOrDefault(recent => SameJobUrl(recent.Url, _recentPendingTopUrl));
        _recentPendingTopUrl = null;
        if (item is null)
            return;

        RecentJobs.Remove(item);
        RecentJobs.Insert(0, item);
        if (SelectedConnection is { } connection)
            SaveJobRefs(connection.Id, "recent", RecentJobs);
        RefreshRefFilters();
    }

    private void LoadSavedJobs(string connectionId)
    {
        _recentPendingTopUrl = null;
        Favorites.Clear();
        RecentJobs.Clear();
        foreach (var item in ReadJobRefs(connectionId, "favorites"))
            Favorites.Add(item);
        foreach (var item in ReadJobRefs(connectionId, "recent"))
            RecentJobs.Add(item);

        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasRecent));
        RefreshRefFilters();
        OnPropertyChanged(nameof(IsSelectedFavorite));
        OnPropertyChanged(nameof(FavoriteActionText));
    }

    private static IEnumerable<JenkinsJobRef> ReadJobRefs(string connectionId, string kind)
    {
        var json = UiPrefs.GetString($"jenkins.{kind}.{connectionId}", string.Empty);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<JenkinsJobRef>();

        try
        {
            return JsonSerializer.Deserialize<JenkinsJobRef[]>(json) ?? Array.Empty<JenkinsJobRef>();
        }
        catch
        {
            return Array.Empty<JenkinsJobRef>();
        }
    }

    private static void SaveJobRefs(string connectionId, string kind, IEnumerable<JenkinsJobRef> items)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;

        try
        {
            var json = JsonSerializer.Serialize(items.ToArray());
            UiPrefs.SetString($"jenkins.{kind}.{connectionId}", json);
        }
        catch
        {
            // 收藏/最近访问保存失败不影响主流程
        }
    }

    private static bool SameJobUrl(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           string.Equals(left.Trim().TrimEnd(char.Parse("/")), right?.Trim().TrimEnd(char.Parse("/")),
               StringComparison.OrdinalIgnoreCase);

    private static string Describe(Exception ex)
        => ex is JenkinsAuthenticationException ? $"认证失败：{ex.Message}" : ex.Message;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queueTimer.Stop();
        CancelPending();
        _client?.Dispose();
        _client = null;
    }
}
