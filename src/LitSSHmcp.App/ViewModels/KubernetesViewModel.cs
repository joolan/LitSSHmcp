using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Kubernetes;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.ViewModels;

public sealed class KubernetesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IConfigService _configService;
    private readonly IUserDialogService _dialogs;
    private readonly IWindowService _windows;
    private readonly IAuditLogService? _audit;
    private readonly KubernetesService _service;
    private readonly ISshService _sshService;
    private readonly Window? _owner;
    private readonly List<KubernetesWorkloadInfo> _allWorkloads = new();
    private readonly List<KubernetesPodInfo> _allPods = new();
    private readonly List<KubernetesEventInfo> _allEvents = new();
    private readonly List<KubernetesServiceInfo> _allServices = new();
    private readonly List<KubernetesIngressInfo> _allIngresses = new();
    private readonly List<KubernetesPvcInfo> _allPvcs = new();
    private readonly List<KubernetesReleaseDifference> _allComparisonRows = new();
    private readonly List<KubernetesIngressDifference> _allIngressComparisonRows = new();
    private readonly CancellationTokenSource _lifetime = new();

    private KubernetesConnectionConfig? _selectedConnection;
    private string _selectedNamespace = "全部命名空间";
    private string _podSearchText = string.Empty;
    private KubernetesPodInfo? _selectedPod;
    private KubernetesNodeInfo? _selectedNode;
    private KubernetesWorkloadInfo? _selectedWorkload;
    private string _version = "—";
    private string _distribution = string.Empty;
    private bool _metricsAvailable;
    private string _metricsStatus = "metrics-server 未检测";
    private int _nodeCount;
    private int _readyNodeCount;
    private int _namespaceCount;
    private int _podCount;
    private int _runningPodCount;
    private int _warningEventCount;
    private string _statusMessage = string.Empty;
    private string _errorMessage = string.Empty;
    private string _podLogs = string.Empty;
    private string _podYaml = string.Empty;
    private string _workloadYaml = string.Empty;
    private bool _isBusy;
    private bool _isPodDetailBusy;
    private bool _isPodLogsBusy;
    private bool _isWorkloadDetailBusy;
    private bool _isServiceDetailBusy;
    private bool _isWriteBusy;
    private bool _suppressPodContainerReload;
    private string _selectedPodContainer = string.Empty;
    private int _networkDetailIndex;
    private bool _loadPreviousLogs;
    private KubernetesServiceInfo? _selectedService;
    private string _serviceYaml = string.Empty;
    private KubernetesConnectionConfig? _compareBaseline;
    private KubernetesConnectionConfig? _compareTarget;
    private bool _showOnlyDifferences = true;
    private bool _isCompareBusy;
    private string _compareStatusMessage = string.Empty;
    private bool _suppressSelectionRefresh;
    private bool _disposed;
    private bool _isActive;
    private bool _activationBusy;
    private bool _connectionsLoaded;
    private bool _connectionsStale = true;
    private DateTime _lastDashboardUtc = DateTime.MinValue;
    private CancellationTokenSource? _refreshCts;
    private static readonly TimeSpan PageRefreshTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PageRefreshTimeout = TimeSpan.FromSeconds(20);
    private readonly DispatcherTimer _autoRefreshTimer;
    private bool _isAutoRefreshEnabled;
    private int _autoRefreshSeconds = 30;

    public KubernetesViewModel(
        IConfigService configService,
        IWindowService windows,
        ISshService sshService,
        IUserDialogService? dialogs = null,
        Window? owner = null,
        IAuditLogService? audit = null)
    {
        _configService = configService;
        _windows = windows;
        _dialogs = dialogs ?? new UserDialogService();
        _audit = audit;
        _sshService = sshService;
        _service = new KubernetesService(sshService);
        _owner = owner;
        AutoRefreshOptions = new[] { 15, 30, 60, 120 };
        _autoRefreshTimer = new DispatcherTimer();
        _autoRefreshTimer.Tick += (_, _) =>
        {
            if (_isActive && SelectedConnection is not null && !IsBusy && !IsWriteBusy)
                _ = RefreshAsync(PageRefreshTimeout);
        };

        RefreshCommand = new RelayCommand(
            _ => AsyncOperation.Run("刷新 K8s 集群", () => RefreshAsync()),
            _ => SelectedConnection is not null && !IsBusy);
        AddConnectionCommand = new RelayCommand(_ => AddConnection(), _ => !IsBusy);
        EditConnectionCommand = new RelayCommand(_ => EditConnection(), _ => SelectedConnection is not null && !IsBusy);
        DeleteConnectionCommand = new RelayCommand(
            _ => AsyncOperation.Run("删除 K8s 连接", DeleteConnectionAsync),
            _ => SelectedConnection is not null && !IsBusy);
        RefreshPodDetailCommand = new RelayCommand(
            _ => AsyncOperation.Run("刷新 Pod 详情", LoadPodDetailsAsync),
            _ => SelectedPod is not null && !IsPodDetailBusy);
        RefreshWorkloadDetailCommand = new RelayCommand(
            _ => AsyncOperation.Run("刷新工作负载详情", LoadWorkloadDetailAsync),
            _ => SelectedWorkload is not null && !IsWorkloadDetailBusy);
        RefreshPodLogsCommand = new RelayCommand(
            _ => AsyncOperation.Run("刷新 Pod 日志", LoadPodLogsAsync),
            _ => SelectedPod is not null && !IsPodLogsBusy);
        RefreshServiceDetailCommand = new RelayCommand(
            _ => AsyncOperation.Run("加载 Service 详情", LoadServiceDetailAsync),
            _ => SelectedService is not null && !IsServiceDetailBusy);
        CompareReleasesCommand = new RelayCommand(
            _ => AsyncOperation.Run("对比多集群发布", CompareReleasesAsync),
            _ => CanCompare && !IsCompareBusy);
        ScaleWorkloadCommand = new RelayCommand(
            _ => AsyncOperation.Run("扩缩容工作负载", ScaleWorkloadAsync),
            _ => CanWrite && SelectedWorkload is { Kind: "Deployment" or "StatefulSet" } && !IsWriteBusy);
        RestartWorkloadCommand = new RelayCommand(
            _ => AsyncOperation.Run("重启工作负载", RestartWorkloadAsync),
            _ => CanWrite && SelectedWorkload is { Kind: "Deployment" or "StatefulSet" or "DaemonSet" } && !IsWriteBusy);
        RollbackWorkloadCommand = new RelayCommand(
            _ => AsyncOperation.Run("回滚工作负载", RollbackWorkloadAsync),
            _ => CanWrite && SelectedWorkload is { Kind: "Deployment" } && !IsWriteBusy);
        DeletePodCommand = new RelayCommand(
            _ => AsyncOperation.Run("删除 Pod", DeleteSelectedPodAsync),
            _ => CanWrite && SelectedPod is not null && !IsWriteBusy);
        CordonNodeCommand = new RelayCommand(
            _ => AsyncOperation.Run("节点 cordon", () => CordonSelectedNodeAsync(true)),
            _ => CanWrite && SelectedNode is not null && !IsWriteBusy);
        UncordonNodeCommand = new RelayCommand(
            _ => AsyncOperation.Run("节点 uncordon", () => CordonSelectedNodeAsync(false)),
            _ => CanWrite && SelectedNode is not null && !IsWriteBusy);

        LoadAutoRefreshPrefs();
    }

    /// <summary>页面是否可见：不可见时停止自动刷新并取消在途刷新，避免切走后台仍持续打网络。</summary>
    public void SetActive(bool active)
    {
        if (_isActive == active)
            return;
        _isActive = active;
        if (active)
        {
            if (_isAutoRefreshEnabled)
            {
                _autoRefreshTimer.Interval = TimeSpan.FromSeconds(AutoRefreshSeconds);
                _autoRefreshTimer.Start();
            }
        }
        else
        {
            _autoRefreshTimer.Stop();
            _refreshCts?.Cancel();
        }
    }

    /// <summary>页面激活：首次加载连接；已加载过则仅在数据过期时刷新，避免来回切菜单重复拉全量资源。</summary>
    public async Task ActivateAsync()
    {
        using var perf = PerfLog.Scope("kubernetes.activate", () => SelectedConnection?.DisplayName ?? "-");
        SetActive(true);
        if (_activationBusy)
            return;

        _activationBusy = true;
        try
        {
            if (!_connectionsLoaded || _connectionsStale)
            {
                await LoadConnectionsAsync();
                return;
            }

            if (SelectedConnection is null || DateTime.UtcNow - _lastDashboardUtc < PageRefreshTtl)
                return;

            await ShowCacheThenRefreshAsync(SelectedConnection);
        }
        finally
        {
            _activationBusy = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<KubernetesConnectionConfig> Connections { get; } = new();
    public RangeObservableCollection<KubernetesNodeInfo> Nodes { get; } = new();
    public RangeObservableCollection<KubernetesNamespaceInfo> Namespaces { get; } = new();
    public RangeObservableCollection<KubernetesWorkloadInfo> Workloads { get; } = new();
    public RangeObservableCollection<KubernetesPodInfo> Pods { get; } = new();
    public RangeObservableCollection<KubernetesPodInfo> RelatedPods { get; } = new();
    public RangeObservableCollection<KubernetesServiceInfo> RelatedServices { get; } = new();
    public RangeObservableCollection<KubernetesIngressInfo> RelatedIngresses { get; } = new();
    public ObservableCollection<string> PodContainers { get; } = new();
    public ObservableCollection<KubernetesServiceInfo> PodRelatedServices { get; } = new();
    public ObservableCollection<KubernetesIngressInfo> PodRelatedIngresses { get; } = new();
    public ObservableCollection<KubernetesEventInfo> PodEvents { get; } = new();
    public ObservableCollection<KubernetesPodInfo> ServiceRelatedPods { get; } = new();
    public ObservableCollection<KubernetesIngressInfo> ServiceRelatedIngresses { get; } = new();
    public RangeObservableCollection<KubernetesHealthIssue> HealthIssues { get; } = new();
    public ObservableCollection<KubernetesReleaseDifference> ComparisonRows { get; } = new();
    public ObservableCollection<KubernetesIngressDifference> IngressComparisonRows { get; } = new();
    public RangeObservableCollection<KubernetesEventInfo> Events { get; } = new();
    public RangeObservableCollection<KubernetesServiceInfo> Services { get; } = new();
    public RangeObservableCollection<KubernetesIngressInfo> Ingresses { get; } = new();
    public RangeObservableCollection<KubernetesPvcInfo> Pvcs { get; } = new();
    public RangeObservableCollection<KubernetesStorageClassInfo> StorageClasses { get; } = new();
    public RangeObservableCollection<string> NamespaceFilters { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand AddConnectionCommand { get; }
    public ICommand EditConnectionCommand { get; }
    public ICommand DeleteConnectionCommand { get; }
    public ICommand RefreshPodDetailCommand { get; }
    public ICommand RefreshWorkloadDetailCommand { get; }
    public ICommand RefreshPodLogsCommand { get; }
    public ICommand RefreshServiceDetailCommand { get; }
    public ICommand CompareReleasesCommand { get; }
    public ICommand ScaleWorkloadCommand { get; }
    public ICommand RestartWorkloadCommand { get; }
    public ICommand RollbackWorkloadCommand { get; }
    public ICommand DeletePodCommand { get; }
    public ICommand CordonNodeCommand { get; }
    public ICommand UncordonNodeCommand { get; }
    public IReadOnlyList<int> AutoRefreshOptions { get; }

    public KubernetesConnectionConfig? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (!Set(ref _selectedConnection, value))
                return;

            OnPropertyChanged(nameof(HasConnection));
            OnPropertyChanged(nameof(HasNoConnection));
            OnPropertyChanged(nameof(ConnectionEnvironment));
            OnPropertyChanged(nameof(ConnectionProvider));
            OnPropertyChanged(nameof(ConnectionAccess));
            OnPropertyChanged(nameof(ConnectionContext));
            OnPropertyChanged(nameof(ConnectionNamespace));
            OnPropertyChanged(nameof(IsHighRiskConnection));
            OnPropertyChanged(nameof(CanWrite));

            _selectedNamespace = string.IsNullOrWhiteSpace(value?.DefaultNamespace)
                ? "全部命名空间"
                : value.DefaultNamespace.Trim();
            OnPropertyChanged(nameof(SelectedNamespace));
            Distribution = string.Empty;

            if (value is not null)
                UiPrefs.SetString("kubernetes.lastConnectionId", value.Id);

            _lastDashboardUtc = DateTime.MinValue;
            if (!_suppressSelectionRefresh && _isActive && value is not null)
                _ = ShowCacheThenRefreshAsync(value);
            RaiseCommandStates();
        }
    }

    public string SelectedNamespace
    {
        get => _selectedNamespace;
        set
        {
            if (!Set(ref _selectedNamespace, value))
                return;
            ApplyFilters();
        }
    }

    public string PodSearchText
    {
        get => _podSearchText;
        set
        {
            if (!Set(ref _podSearchText, value))
                return;
            ApplyPodFilter();
        }
    }

    public KubernetesPodInfo? SelectedPod
    {
        get => _selectedPod;
        set
        {
            if (!Set(ref _selectedPod, value))
                return;
            OnPropertyChanged(nameof(HasSelectedPod));
            OnPropertyChanged(nameof(SelectedPodTitle));
            PodLogs = string.Empty;
            PodYaml = string.Empty;
            PodContainers.Clear();
            PodRelatedServices.Clear();
            PodRelatedIngresses.Clear();
            PodEvents.Clear();
            OnPropertyChanged(nameof(HasPodRelatedServices));
            OnPropertyChanged(nameof(HasPodRelatedIngresses));
            OnPropertyChanged(nameof(HasPodEvents));
            _suppressPodContainerReload = true;
            SelectedPodContainer = value?.Containers.FirstOrDefault() ?? string.Empty;
            _suppressPodContainerReload = false;
            _loadPreviousLogs = false;
            OnPropertyChanged(nameof(LoadPreviousLogs));
            if (value is not null)
            {
                foreach (var container in value.Containers)
                    PodContainers.Add(container);
                AsyncOperation.Run("加载 Pod 详情", LoadPodDetailsAsync);
            }
            RaiseCommandStates();
        }
    }

    public bool IsAutoRefreshEnabled
    {
        get => _isAutoRefreshEnabled;
        set
        {
            if (!Set(ref _isAutoRefreshEnabled, value))
                return;
            if (value && _isActive)
            {
                _autoRefreshTimer.Interval = TimeSpan.FromSeconds(AutoRefreshSeconds);
                _autoRefreshTimer.Start();
            }
            else
            {
                _autoRefreshTimer.Stop();
            }
            UiPrefs.SetString("kubernetes.autoRefresh", value ? "true" : "false");
        }
    }

    public int AutoRefreshSeconds
    {
        get => _autoRefreshSeconds;
        set
        {
            var normalized = AutoRefreshOptions.Contains(value) ? value : 30;
            if (!Set(ref _autoRefreshSeconds, normalized))
                return;
            _autoRefreshTimer.Interval = TimeSpan.FromSeconds(normalized);
            UiPrefs.SetString("kubernetes.autoRefreshSeconds", normalized.ToString());
        }
    }
    public int NetworkDetailIndex
    {
        get => _networkDetailIndex;
        set => Set(ref _networkDetailIndex, value);
    }

    public string SelectedPodContainer
    {
        get => _selectedPodContainer;
        set
        {
            if (!Set(ref _selectedPodContainer, value))
                return;
            if (!_suppressPodContainerReload && SelectedPod is not null)
                AsyncOperation.Run("加载 Pod 日志", LoadPodLogsAsync);
        }
    }

    public bool LoadPreviousLogs
    {
        get => _loadPreviousLogs;
        set
        {
            if (!Set(ref _loadPreviousLogs, value))
                return;
            if (SelectedPod is not null)
                AsyncOperation.Run("加载 Pod 日志", LoadPodLogsAsync);
        }
    }

    public KubernetesServiceInfo? SelectedService
    {
        get => _selectedService;
        set
        {
            if (!Set(ref _selectedService, value))
                return;
            OnPropertyChanged(nameof(HasSelectedService));
            OnPropertyChanged(nameof(SelectedServiceTitle));
            ServiceYaml = string.Empty;
            ServiceRelatedPods.Clear();
            ServiceRelatedIngresses.Clear();
            OnPropertyChanged(nameof(HasServiceRelatedPods));
            OnPropertyChanged(nameof(HasServiceRelatedIngresses));
            NetworkDetailIndex = value is null ? 0 : 1;
            if (value is not null)
                AsyncOperation.Run("加载 Service 详情", LoadServiceDetailAsync);
            RaiseCommandStates();
        }
    }

    public KubernetesConnectionConfig? CompareBaseline
    {
        get => _compareBaseline;
        set
        {
            if (!Set(ref _compareBaseline, value))
                return;
            ClearComparison();
            RaiseCommandStates();
        }
    }

    public KubernetesConnectionConfig? CompareTarget
    {
        get => _compareTarget;
        set
        {
            if (!Set(ref _compareTarget, value))
                return;
            ClearComparison();
            RaiseCommandStates();
        }
    }

    public bool ShowOnlyDifferences
    {
        get => _showOnlyDifferences;
        set
        {
            if (!Set(ref _showOnlyDifferences, value))
                return;
            ApplyComparisonFilter();
        }
    }

    public bool IsCompareBusy
    {
        get => _isCompareBusy;
        private set
        {
            if (!Set(ref _isCompareBusy, value))
                return;
            RaiseCommandStates();
        }
    }

    public string CompareStatusMessage
    {
        get => _compareStatusMessage;
        private set
        {
            if (!Set(ref _compareStatusMessage, value))
                return;
            OnPropertyChanged(nameof(HasCompareStatus));
        }
    }

    public bool HasCompareStatus => CompareStatusMessage.Length > 0;
    public bool CanWrite => SelectedConnection is { ReadOnly: false };
    public bool IsWriteBusy
    {
        get => _isWriteBusy;
        private set
        {
            if (!Set(ref _isWriteBusy, value))
                return;
            RaiseCommandStates();
        }
    }

    public bool CanCompare => CompareBaseline is not null &&
                              CompareTarget is not null &&
                              CompareBaseline.Id != CompareTarget.Id;
    public bool HasComparisonRows => ComparisonRows.Count > 0;
    public bool HasIngressComparisonRows => IngressComparisonRows.Count > 0;
    public int ComparisonBaselineCount => _allComparisonRows.Count(item => item.BaselineImage.Length > 0 || item.BaselineReplicas.Length > 0);
    public int ComparisonTargetCount => _allComparisonRows.Count(item => item.TargetImage.Length > 0 || item.TargetReplicas.Length > 0);
    public int ComparisonDifferenceCount => _allComparisonRows.Count(item => item.IsDifferent);
    public int ComparisonMissingCount => _allComparisonRows.Count(item => item.Difference.Contains("仅", StringComparison.Ordinal));
    public bool HasSelectedService => SelectedService is not null;
    public string SelectedServiceTitle => SelectedService is null
        ? "选择一个 Service 查看详情"
        : $"Service / {SelectedService.Namespace} / {SelectedService.Name}";
    public bool HasPodRelatedServices => PodRelatedServices.Count > 0;
    public bool HasPodRelatedIngresses => PodRelatedIngresses.Count > 0;
    public bool HasPodEvents => PodEvents.Count > 0;
    public bool HasServiceRelatedPods => ServiceRelatedPods.Count > 0;
    public bool HasServiceRelatedIngresses => ServiceRelatedIngresses.Count > 0;

    public KubernetesNodeInfo? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value))
                return;
            RaiseCommandStates();
        }
    }

    public KubernetesWorkloadInfo? SelectedWorkload
    {
        get => _selectedWorkload;
        set
        {
            if (!Set(ref _selectedWorkload, value))
                return;
            OnPropertyChanged(nameof(HasSelectedWorkload));
            OnPropertyChanged(nameof(SelectedWorkloadTitle));
            WorkloadYaml = string.Empty;
            RelatedPods.Clear();
            RelatedServices.Clear();
            RelatedIngresses.Clear();
            OnPropertyChanged(nameof(HasRelatedPods));
            OnPropertyChanged(nameof(HasRelatedServices));
            OnPropertyChanged(nameof(HasRelatedIngresses));
            if (value is not null)
                AsyncOperation.Run("加载工作负载详情", LoadWorkloadDetailAsync);
            RaiseCommandStates();
        }
    }

    public bool HasSelectedWorkload => SelectedWorkload is not null;
    public bool HasRelatedPods => RelatedPods.Count > 0;
    public bool HasRelatedServices => RelatedServices.Count > 0;
    public bool HasRelatedIngresses => RelatedIngresses.Count > 0;
    public bool HasHealthIssues => HealthIssues.Count > 0;
    public int HealthIssueCount => HealthIssues.Count;
    public int HealthCriticalCount => HealthIssues.Count(item => item.Severity == "严重");
    public int HealthWarningCount => HealthIssues.Count(item => item.Severity == "警告");
    public int HealthNodeIssueCount => HealthIssues.Count(item => item.Category == "节点");
    public int HealthPodIssueCount => HealthIssues.Count(item => item.Category == "Pod");
    public int HealthWorkloadIssueCount => HealthIssues.Count(item => item.Category == "工作负载");
    public int HealthStorageIssueCount => HealthIssues.Count(item => item.Category == "存储");
    public string HealthStatusText => HealthCriticalCount > 0
        ? $"{HealthCriticalCount} 个严重问题"
        : HealthWarningCount > 0 ? $"{HealthWarningCount} 个警告" : "未发现异常";
    public string SelectedWorkloadTitle => SelectedWorkload is null
        ? "选择一个工作负载查看详情"
        : $"{SelectedWorkload.Kind} / {SelectedWorkload.Namespace} / {SelectedWorkload.Name}";

    public string Version
    {
        get => _version;
        private set => Set(ref _version, value);
    }

    public string Distribution
    {
        get => _distribution;
        private set
        {
            if (!Set(ref _distribution, value))
                return;
            OnPropertyChanged(nameof(HasDetectedDistribution));
            OnPropertyChanged(nameof(DistributionDisplay));
            OnPropertyChanged(nameof(ConnectionProvider));
        }
    }

    public bool MetricsAvailable => _metricsAvailable;
    public string MetricsStatus => _metricsStatus;

    public bool HasDetectedDistribution => Distribution.Length > 0;
    public string DistributionDisplay => Distribution.Length == 0 ? "未检测" : Distribution;

    public int NodeCount
    {
        get => _nodeCount;
        private set => Set(ref _nodeCount, value);
    }

    public int ReadyNodeCount
    {
        get => _readyNodeCount;
        private set => Set(ref _readyNodeCount, value);
    }

    public int NamespaceCount
    {
        get => _namespaceCount;
        private set => Set(ref _namespaceCount, value);
    }

    public int PodCount
    {
        get => _podCount;
        private set => Set(ref _podCount, value);
    }

    public int RunningPodCount
    {
        get => _runningPodCount;
        private set => Set(ref _runningPodCount, value);
    }

    public int WarningEventCount
    {
        get => _warningEventCount;
        private set => Set(ref _warningEventCount, value);
    }

    public string NodeHealth => NodeCount == 0 ? "—" : $"{ReadyNodeCount}/{NodeCount}";
    public string PodHealth => PodCount == 0 ? "—" : $"{RunningPodCount}/{PodCount}";
    public string WarningHealth => WarningEventCount == 0 ? "正常" : $"{WarningEventCount} 条";
    public bool HasConnection => SelectedConnection is not null;
    public bool HasNoConnection => SelectedConnection is null;
    public bool IsHighRiskConnection => SelectedConnection?.IsHighRisk == true;
    public string ConnectionEnvironment => SelectedConnection?.EnvironmentDisplay ?? "未选择集群";
    public string ConnectionProvider
    {
        get
        {
            if (SelectedConnection is null)
                return string.Empty;
            var provider = SelectedConnection.ProviderDisplay;
            return Distribution.Length > 0 &&
                   !string.Equals(provider, Distribution, StringComparison.OrdinalIgnoreCase)
                ? $"{provider} · {Distribution}"
                : provider;
        }
    }
    public string ConnectionAccess => SelectedConnection?.AccessDisplay ?? string.Empty;
    public string ConnectionContext => SelectedConnection?.ContextDisplay ?? string.Empty;
    public string ConnectionNamespace => SelectedConnection?.NamespaceDisplay ?? string.Empty;
    public bool HasSelectedPod => SelectedPod is not null;
    public string SelectedPodTitle => SelectedPod is null ? "选择一个 Pod 查看详情" : $"{SelectedPod.Namespace} / {SelectedPod.Name}";
    public bool HasError => ErrorMessage.Length > 0;
    public bool HasStatus => StatusMessage.Length > 0;
    public bool HasNodes => Nodes.Count > 0;
    public bool HasPods => Pods.Count > 0;
    public bool HasWorkloads => Workloads.Count > 0;
    public bool HasEvents => Events.Count > 0;
    public bool HasServices => Services.Count > 0;
    public bool HasIngresses => Ingresses.Count > 0;
    public bool HasPvcs => Pvcs.Count > 0;
    public bool HasStorageClasses => StorageClasses.Count > 0;

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (!Set(ref _statusMessage, value))
                return;
            OnPropertyChanged(nameof(HasStatus));
        }
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

    public string PodLogs
    {
        get => _podLogs;
        private set => Set(ref _podLogs, value);
    }

    public string PodYaml
    {
        get => _podYaml;
        private set => Set(ref _podYaml, value);
    }

    public string WorkloadYaml
    {
        get => _workloadYaml;
        private set => Set(ref _workloadYaml, value);
    }

    public string ServiceYaml
    {
        get => _serviceYaml;
        private set => Set(ref _serviceYaml, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value))
                return;
            RaiseCommandStates();
        }
    }

    public bool IsPodDetailBusy
    {
        get => _isPodDetailBusy;
        private set
        {
            if (!Set(ref _isPodDetailBusy, value))
                return;
            RaiseCommandStates();
        }
    }

    public bool IsWorkloadDetailBusy
    {
        get => _isWorkloadDetailBusy;
        private set
        {
            if (!Set(ref _isWorkloadDetailBusy, value))
                return;
            RaiseCommandStates();
        }
    }

    public bool IsPodLogsBusy
    {
        get => _isPodLogsBusy;
        private set
        {
            if (!Set(ref _isPodLogsBusy, value))
                return;
            RaiseCommandStates();
        }
    }

    public bool IsServiceDetailBusy
    {
        get => _isServiceDetailBusy;
        private set
        {
            if (!Set(ref _isServiceDetailBusy, value))
                return;
            RaiseCommandStates();
        }
    }

    public void ReloadConfig()
    {
        _connectionsStale = true;
        if (_isActive)
            _ = LoadConnectionsAsync();
    }

    public async Task LoadConnectionsAsync()
    {
        var previousId = SelectedConnection?.Id ?? UiPrefs.GetString("kubernetes.lastConnectionId", string.Empty);
        try
        {
            var config = await _configService.LoadConfigAsync().ConfigureAwait(true);
            _sshServers = config.Servers ?? Array.Empty<SshServerConfig>();
            var connections = (config.Kubernetes?.Connections ?? Array.Empty<KubernetesConnectionConfig>())
                .Where(item => item is not null && item.Enabled)
                .ToArray();

            _suppressSelectionRefresh = true;
            Connections.Clear();
            foreach (var connection in connections)
                Connections.Add(connection);

            var selected = connections.FirstOrDefault(item => item.Id == previousId)
                ?? connections.FirstOrDefault(item => item.Id == config.Kubernetes?.DefaultConnectionId)
                ?? connections.FirstOrDefault();
            _selectedConnection = null;
            NotifySelectionChanged();
            SelectedConnection = selected;
            _suppressSelectionRefresh = false;
            _connectionsLoaded = true;
            _connectionsStale = false;

            if (selected is null)
            {
                ClearData();
                StatusMessage = "还没有可用的 K8s 连接，请先添加连接。";
            }
            else if (_isActive)
            {
                _ = ShowCacheThenRefreshAsync(selected);
            }
        }
        catch (Exception ex)
        {
            _connectionsStale = true;
            ErrorMessage = $"加载 K8s 连接失败：{ex.Message}";
            StatusMessage = "无法读取 K8s 连接配置。";
        }
        finally
        {
            _suppressSelectionRefresh = false;
        }
    }

    private async Task ShowCacheThenRefreshAsync(KubernetesConnectionConfig? connection)
    {
        if (connection is null)
        {
            ClearData();
            return;
        }

        await TryShowCachedDashboardAsync(connection.Id);
        if (!_isActive || !ReferenceEquals(SelectedConnection, connection))
            return;

        await RefreshAsync(PageRefreshTimeout);
    }

    private async Task TryShowCachedDashboardAsync(string connectionId)
    {
        if (!_isActive || DateTime.UtcNow - _lastDashboardUtc < PageRefreshTtl)
            return;

        var cached = await PageDiskCache.TryLoadAsync<KubernetesDashboard>("kubernetes", connectionId);
        if (!cached.Found || cached.Value is not { } dashboard)
            return;
        if (!_isActive || !string.Equals(SelectedConnection?.Id, connectionId, StringComparison.Ordinal))
            return;

        ApplyDashboard(dashboard, fromCache: true);
        StatusMessage = $"已显示 {cached.SavedUtc.ToLocalTime():MM-dd HH:mm} 的缓存，正在刷新…";
    }

    private void ApplyDashboard(KubernetesDashboard dashboard, bool fromCache)
    {
        Version = dashboard.Version;
        Distribution = dashboard.Distribution;
        NodeCount = dashboard.NodeCount;
        ReadyNodeCount = dashboard.ReadyNodeCount;
        NamespaceCount = dashboard.NamespaceCount;
        PodCount = dashboard.PodCount;
        RunningPodCount = dashboard.RunningPodCount;
        WarningEventCount = dashboard.WarningEventCount;

        Replace(Nodes, dashboard.Nodes);
        Replace(Namespaces, dashboard.Namespaces);
        _allWorkloads.Clear();
        _allWorkloads.AddRange(dashboard.Workloads);
        _allPods.Clear();
        _allPods.AddRange(dashboard.Pods);
        _allEvents.Clear();
        _allEvents.AddRange(dashboard.Events);
        _allServices.Clear();
        _allServices.AddRange(dashboard.Services);
        _allIngresses.Clear();
        _allIngresses.AddRange(dashboard.Ingresses);
        _allPvcs.Clear();
        _allPvcs.AddRange(dashboard.Pvcs);
        Replace(StorageClasses, dashboard.StorageClasses);

        RebuildNamespaceFilters();
        ApplyFilters();
        ApplyMetrics(dashboard.Metrics);
        BuildHealth(dashboard);
        OnPropertyChanged(nameof(NodeHealth));
        OnPropertyChanged(nameof(PodHealth));
        OnPropertyChanged(nameof(WarningHealth));
        OnPropertyChanged(nameof(HasNodes));
        OnPropertyChanged(nameof(HasWorkloads));
        OnPropertyChanged(nameof(HasPods));
        OnPropertyChanged(nameof(HasEvents));
        OnPropertyChanged(nameof(HasServices));
        OnPropertyChanged(nameof(HasIngresses));
        OnPropertyChanged(nameof(HasPvcs));
        OnPropertyChanged(nameof(HasStorageClasses));

        if (!fromCache)
        {
            StatusMessage = dashboard.WarningEventCount > 0
                ? $"已加载 {dashboard.PodCount} 个 Pod，{dashboard.WarningEventCount} 条 Warning 事件。{MetricsStatus}"
                : $"已加载 {dashboard.PodCount} 个 Pod，集群状态正常。{MetricsStatus}";
        }
    }

    private async Task RefreshAsync(TimeSpan? timeout = null)
    {
        using var perf = PerfLog.Scope("kubernetes.refresh", () => SelectedConnection?.DisplayName ?? "-");
        if (IsBusy)
            return;
        var connection = SelectedConnection;
        if (connection is null)
        {
            ClearData();
            return;
        }

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (timeout is { } limit)
            cts.CancelAfter(limit);
        _refreshCts = cts;
        var ct = cts.Token;

        var server = FindSshServer(connection);
        SelectedWorkload = null;
        SelectedPod = null;
        SelectedService = null;
        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = $"正在读取 {connection.DisplayName}…";
        try
        {
            var dashboard = await _service.LoadDashboardAsync(connection, server, ct);
            if (!_isActive || !ReferenceEquals(SelectedConnection, connection))
                return;

            ApplyDashboard(dashboard, fromCache: false);
            _lastDashboardUtc = DateTime.UtcNow;
            _ = PageDiskCache.SaveAsync("kubernetes", connection.Id, dashboard);
        }
        catch (OperationCanceledException)
        {
            if (_isActive)
                StatusMessage = "已取消刷新。";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"读取 {connection.DisplayName} 失败：{ex.Message}";
            StatusMessage = "请检查 kubeconfig、context、网络或 SSH 跳板配置。";
        }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(_refreshCts, cts))
                _refreshCts = null;
            cts.Dispose();
        }
    }

    private async Task LoadPodDetailsAsync()
    {
        var connection = SelectedConnection;
        var pod = SelectedPod;
        if (connection is null || pod is null)
            return;

        var server = FindSshServer(connection);
        IsPodDetailBusy = true;
        IsPodLogsBusy = true;
        try
        {
            BuildPodAssociations(pod);
            var logsTask = _service.GetPodLogsAsync(
                connection, server, pod, SelectedPodContainer, LoadPreviousLogs, _lifetime.Token);
            var yamlTask = _service.GetPodYamlAsync(connection, server, pod, _lifetime.Token);
            await Task.WhenAll(logsTask, yamlTask).ConfigureAwait(true);
            if (!ReferenceEquals(SelectedPod, pod))
                return;
            PodLogs = await logsTask.ConfigureAwait(true);
            PodYaml = await yamlTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PodLogs = $"读取详情失败：{ex.Message}";
            PodYaml = PodLogs;
        }
        finally
        {
            IsPodLogsBusy = false;
            IsPodDetailBusy = false;
        }
    }

    private async Task LoadPodLogsAsync()
    {
        var connection = SelectedConnection;
        var pod = SelectedPod;
        if (connection is null || pod is null)
            return;

        var server = FindSshServer(connection);
        IsPodLogsBusy = true;
        try
        {
            var logs = await _service.GetPodLogsAsync(
                connection, server, pod, SelectedPodContainer, LoadPreviousLogs, _lifetime.Token);
            if (ReferenceEquals(SelectedPod, pod))
                PodLogs = logs;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PodLogs = $"读取日志失败：{ex.Message}";
        }
        finally
        {
            IsPodLogsBusy = false;
        }
    }

    private void BuildPodAssociations(KubernetesPodInfo pod)
    {
        PodRelatedServices.Clear();
        PodRelatedIngresses.Clear();
        PodEvents.Clear();

        foreach (var service in _allServices.Where(service =>
                     service.Namespace == pod.Namespace &&
                     service.Selector.Count > 0 &&
                     MatchesSelector(pod.Labels, service.Selector)))
            PodRelatedServices.Add(service);

        var serviceNames = PodRelatedServices.Select(service => service.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var ingress in _allIngresses.Where(ingress =>
                     ingress.Namespace == pod.Namespace &&
                     ingress.ServiceNames.Any(serviceNames.Contains)))
            PodRelatedIngresses.Add(ingress);

        foreach (var evt in _allEvents.Where(evt =>
                     evt.Namespace == pod.Namespace &&
                     (evt.Object == $"Pod/{pod.Name}" ||
                      evt.Object.EndsWith("/" + pod.Name, StringComparison.Ordinal))))
            PodEvents.Add(evt);

        OnPropertyChanged(nameof(HasPodRelatedServices));
        OnPropertyChanged(nameof(HasPodRelatedIngresses));
        OnPropertyChanged(nameof(HasPodEvents));
    }

    private async Task LoadServiceDetailAsync()
    {
        var connection = SelectedConnection;
        var serviceInfo = SelectedService;
        if (connection is null || serviceInfo is null)
            return;

        var server = FindSshServer(connection);
        IsServiceDetailBusy = true;
        try
        {
            ServiceRelatedPods.Clear();
            ServiceRelatedIngresses.Clear();
            if (serviceInfo.Selector.Count > 0)
            {
                foreach (var pod in _allPods.Where(pod =>
                             pod.Namespace == serviceInfo.Namespace &&
                             MatchesSelector(pod.Labels, serviceInfo.Selector)))
                    ServiceRelatedPods.Add(pod);
            }

            foreach (var ingress in _allIngresses.Where(ingress =>
                         ingress.Namespace == serviceInfo.Namespace &&
                         ingress.ServiceNames.Contains(serviceInfo.Name, StringComparer.Ordinal)))
                ServiceRelatedIngresses.Add(ingress);

            OnPropertyChanged(nameof(HasServiceRelatedPods));
            OnPropertyChanged(nameof(HasServiceRelatedIngresses));
            var yaml = await _service.GetServiceYamlAsync(connection, server, serviceInfo, _lifetime.Token);
            if (ReferenceEquals(SelectedService, serviceInfo))
                ServiceYaml = yaml;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ServiceYaml = $"读取详情失败：{ex.Message}";
        }
        finally
        {
            IsServiceDetailBusy = false;
        }
    }
    private async Task LoadWorkloadDetailAsync()
    {
        var connection = SelectedConnection;
        var workload = SelectedWorkload;
        if (connection is null || workload is null)
            return;

        var server = FindSshServer(connection);
        IsWorkloadDetailBusy = true;
        try
        {
            var selector = workload.Selector;
            var pods = _allPods
                .Where(pod => pod.Namespace == workload.Namespace &&
                              (string.Equals(pod.OwnerName, workload.Name, StringComparison.Ordinal) ||
                               pod.OwnerName.StartsWith(workload.Name + "-", StringComparison.Ordinal) ||
                               (selector.Count > 0 && MatchesSelector(pod.Labels, selector))))
                .OrderBy(pod => pod.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var services = _allServices
                .Where(service => service.Namespace == workload.Namespace &&
                                  service.Selector.Count > 0 &&
                                  ((selector.Count > 0 && MatchesSelector(selector, service.Selector)) ||
                                   pods.Any(pod => MatchesSelector(pod.Labels, service.Selector))))
                .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var serviceNames = services.Select(service => service.Name).ToHashSet(StringComparer.Ordinal);
            var ingresses = _allIngresses
                .Where(ingress => ingress.Namespace == workload.Namespace &&
                                  ingress.ServiceNames.Any(serviceNames.Contains))
                .OrderBy(ingress => ingress.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!ReferenceEquals(SelectedWorkload, workload))
                return;

            Replace(RelatedPods, pods);
            Replace(RelatedServices, services);
            Replace(RelatedIngresses, ingresses);
            OnPropertyChanged(nameof(HasRelatedPods));
            OnPropertyChanged(nameof(HasRelatedServices));
            OnPropertyChanged(nameof(HasRelatedIngresses));

            var yaml = await _service.GetWorkloadYamlAsync(connection, server, workload, _lifetime.Token);
            if (ReferenceEquals(SelectedWorkload, workload))
                WorkloadYaml = yaml;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            WorkloadYaml = $"读取详情失败：{ex.Message}";
        }
        finally
        {
            IsWorkloadDetailBusy = false;
        }
    }

    private static bool MatchesSelector(
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, string> selector)
    {
        if (selector.Count == 0)
            return false;
        foreach (var pair in selector)
        {
            if (!labels.TryGetValue(pair.Key, out var value) || value != pair.Value)
                return false;
        }
        return true;
    }
    private async Task CompareReleasesAsync()
    {
        var baseline = CompareBaseline;
        var target = CompareTarget;
        if (baseline is null || target is null || baseline.Id == target.Id)
        {
            CompareStatusMessage = "请选择两个不同的集群作为基线和目标。";
            return;
        }

        IsCompareBusy = true;
        CompareStatusMessage = $"正在读取 {baseline.DisplayName} 和 {target.DisplayName}…";
        try
        {
            var baselineTask = _service.LoadReleaseInventoryAsync(
                baseline, FindSshServer(baseline), _lifetime.Token);
            var targetTask = _service.LoadReleaseInventoryAsync(
                target, FindSshServer(target), _lifetime.Token);
            await Task.WhenAll(baselineTask, targetTask).ConfigureAwait(true);
            if (!ReferenceEquals(CompareBaseline, baseline) || !ReferenceEquals(CompareTarget, target))
                return;

            BuildReleaseComparison(await baselineTask.ConfigureAwait(true), await targetTask.ConfigureAwait(true));
            CompareStatusMessage = $"对比完成：{ComparisonDifferenceCount} 项差异，{ComparisonMissingCount} 项单边存在。";
        }
        catch (OperationCanceledException)
        {
            CompareStatusMessage = "对比已取消。";
        }
        catch (Exception ex)
        {
            CompareStatusMessage = $"对比失败：{ex.Message}";
        }
        finally
        {
            IsCompareBusy = false;
        }
    }

    private void BuildReleaseComparison(KubernetesReleaseInventory baseline, KubernetesReleaseInventory target)
    {
        _allComparisonRows.Clear();
        _allIngressComparisonRows.Clear();

        var baselineWorkloads = baseline.Workloads
            .GroupBy(WorkloadKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var targetWorkloads = target.Workloads
            .GroupBy(WorkloadKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var key in baselineWorkloads.Keys.Union(targetWorkloads.Keys).OrderBy(key => key, StringComparer.Ordinal))
        {
            baselineWorkloads.TryGetValue(key, out var source);
            targetWorkloads.TryGetValue(key, out var destination);
            var differences = new List<string>();
            if (source is null)
                differences.Add("仅目标");
            else if (destination is null)
                differences.Add("仅基线");
            else
            {
                if (source.Image != destination.Image)
                    differences.Add("镜像");
                if (source.Ready != destination.Ready)
                    differences.Add("副本");
                if (source.Resources != destination.Resources)
                    differences.Add("资源");
                if (source.Status != destination.Status)
                    differences.Add("状态");
            }

            var sample = source ?? destination!;
            _allComparisonRows.Add(new KubernetesReleaseDifference
            {
                Namespace = sample.Namespace,
                Kind = sample.Kind,
                Name = sample.Name,
                BaselineImage = source?.Image ?? string.Empty,
                TargetImage = destination?.Image ?? string.Empty,
                BaselineReplicas = source?.Ready ?? string.Empty,
                TargetReplicas = destination?.Ready ?? string.Empty,
                BaselineResources = source?.Resources ?? string.Empty,
                TargetResources = destination?.Resources ?? string.Empty,
                Difference = differences.Count == 0 ? "一致" : string.Join(", ", differences),
                IsDifferent = differences.Count > 0
            });
        }

        var baselineIngresses = baseline.Ingresses
            .GroupBy(item => $"{item.Namespace}/{item.Name}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var targetIngresses = target.Ingresses
            .GroupBy(item => $"{item.Namespace}/{item.Name}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var key in baselineIngresses.Keys.Union(targetIngresses.Keys).OrderBy(key => key, StringComparer.Ordinal))
        {
            baselineIngresses.TryGetValue(key, out var source);
            targetIngresses.TryGetValue(key, out var destination);
            var differences = new List<string>();
            if (source is null)
                differences.Add("仅目标");
            else if (destination is null)
                differences.Add("仅基线");
            else
            {
                if (source.ClassName != destination.ClassName)
                    differences.Add("Class");
                if (source.Hosts != destination.Hosts)
                    differences.Add("Host");
            }

            var sample = source ?? destination!;
            _allIngressComparisonRows.Add(new KubernetesIngressDifference
            {
                Namespace = sample.Namespace,
                Name = sample.Name,
                BaselineClass = source?.ClassName ?? string.Empty,
                TargetClass = destination?.ClassName ?? string.Empty,
                BaselineHosts = source?.Hosts ?? string.Empty,
                TargetHosts = destination?.Hosts ?? string.Empty,
                Difference = differences.Count == 0 ? "一致" : string.Join(", ", differences),
                IsDifferent = differences.Count > 0
            });
        }

        ApplyComparisonFilter();
    }

    private void ClearComparison()
    {
        _allComparisonRows.Clear();
        _allIngressComparisonRows.Clear();
        CompareStatusMessage = string.Empty;
        ApplyComparisonFilter();
    }

    private void ApplyComparisonFilter()
    {
        ComparisonRows.Clear();
        foreach (var row in _allComparisonRows.Where(row => !ShowOnlyDifferences || row.IsDifferent))
            ComparisonRows.Add(row);
        IngressComparisonRows.Clear();
        foreach (var row in _allIngressComparisonRows.Where(row => !ShowOnlyDifferences || row.IsDifferent))
            IngressComparisonRows.Add(row);

        OnPropertyChanged(nameof(HasComparisonRows));
        OnPropertyChanged(nameof(HasIngressComparisonRows));
        OnPropertyChanged(nameof(ComparisonBaselineCount));
        OnPropertyChanged(nameof(ComparisonTargetCount));
        OnPropertyChanged(nameof(ComparisonDifferenceCount));
        OnPropertyChanged(nameof(ComparisonMissingCount));
    }

    private static string WorkloadKey(KubernetesWorkloadInfo workload)
        => $"{workload.Kind}/{workload.Namespace}/{workload.Name}";
    private void ApplyMetrics(KubernetesMetricsSnapshot? metrics)
    {
        foreach (var node in Nodes)
        {
            node.CpuDisplay = FormatCpu(ParseCpuCores(node.Cpu));
            node.MemoryDisplay = FormatBytes(ParseBytes(node.Memory));
            node.CpuUsage = string.Empty;
            node.MemoryUsage = string.Empty;
            node.CpuUsageDisplay = string.Empty;
            node.MemoryUsageDisplay = string.Empty;
            node.CpuUsageTooltip = string.Empty;
            node.MemoryUsageTooltip = string.Empty;
        }
        foreach (var pod in Pods)
        {
            pod.CpuUsage = string.Empty;
            pod.MemoryUsage = string.Empty;
            pod.CpuUsageDisplay = string.Empty;
            pod.MemoryUsageDisplay = string.Empty;
            pod.CpuUsageTooltip = string.Empty;
            pod.MemoryUsageTooltip = string.Empty;
        }

        if (metrics is null)
        {
            _metricsAvailable = false;
            _metricsStatus = " · metrics-server 不可用";
            OnPropertyChanged(nameof(MetricsAvailable));
            OnPropertyChanged(nameof(MetricsStatus));
            return;
        }

        var nodeMetrics = metrics.Nodes.ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (!nodeMetrics.TryGetValue(node.Name, out var metric))
                continue;

            node.CpuUsage = metric.Cpu;
            node.MemoryUsage = metric.Memory;
            var cpuCapacity = ParseCpuCores(node.Cpu);
            var cpuUsage = ParseCpuCores(metric.Cpu);
            var memoryCapacity = ParseBytes(node.Memory);
            var memoryUsage = ParseBytes(metric.Memory);
            node.CpuUsageDisplay = FormatUsage(FormatCpu(cpuUsage), cpuUsage, cpuCapacity);
            node.MemoryUsageDisplay = FormatUsage(FormatBytes(memoryUsage), memoryUsage, memoryCapacity);
            node.CpuUsageTooltip = FormatTooltip(metric.Cpu, node.Cpu, cpuUsage, cpuCapacity);
            node.MemoryUsageTooltip = FormatTooltip(metric.Memory, node.Memory, memoryUsage, memoryCapacity);
        }

        var podMetrics = metrics.Pods.ToDictionary(item => $"{item.Namespace}/{item.Name}", StringComparer.Ordinal);
        foreach (var pod in Pods)
        {
            if (!podMetrics.TryGetValue($"{pod.Namespace}/{pod.Name}", out var metric))
                continue;

            pod.CpuUsage = metric.Cpu;
            pod.MemoryUsage = metric.Memory;
            var cpuUsage = ParseCpuCores(metric.Cpu);
            var memoryUsage = ParseBytes(metric.Memory);
            var cpuLimit = ParseCpuCores(pod.CpuLimit);
            var memoryLimit = ParseBytes(pod.MemoryLimit);
            pod.CpuUsageDisplay = FormatUsage(FormatCpu(cpuUsage), cpuUsage, cpuLimit);
            pod.MemoryUsageDisplay = FormatUsage(FormatBytes(memoryUsage), memoryUsage, memoryLimit);
            pod.CpuUsageTooltip = cpuLimit > 0
                ? FormatTooltip(metric.Cpu, pod.CpuLimit, cpuUsage, cpuLimit)
                : $"CPU {metric.Cpu}";
            pod.MemoryUsageTooltip = memoryLimit > 0
                ? FormatTooltip(metric.Memory, pod.MemoryLimit, memoryUsage, memoryLimit)
                : $"内存 {metric.Memory}";
        }

        _metricsAvailable = metrics.Nodes.Count > 0 || metrics.Pods.Count > 0;
        _metricsStatus = _metricsAvailable
            ? $" · metrics-server {metrics.Nodes.Count} 节点/{metrics.Pods.Count} Pod"
            : " · metrics-server 无数据";
        OnPropertyChanged(nameof(MetricsAvailable));
        OnPropertyChanged(nameof(MetricsStatus));
    }

    private static string FormatUsage(string formattedUsage, double usage, double capacity)
    {
        if (usage <= 0)
            return string.Empty;
        var percent = capacity > 0 ? usage / capacity * 100 : 0;
        return percent > 0
            ? $"{formattedUsage} ({percent:0.#}%)"
            : formattedUsage;
    }

    private static string FormatTooltip(string rawUsage, string rawCapacity, double usage, double capacity)
    {
        var percent = capacity > 0 ? usage / capacity * 100 : 0;
        return percent > 0
            ? $"{rawUsage} / {rawCapacity} ({percent:0.#}%)"
            : $"{rawUsage} / {rawCapacity}";
    }

    private static double ParseCpuCores(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        var text = value.Trim();
        var multiplier = 1d;
        if (text.EndsWith("n", StringComparison.OrdinalIgnoreCase))
            multiplier = 1d / 1_000_000_000d;
        else if (text.EndsWith("u", StringComparison.OrdinalIgnoreCase) ||
                 text.EndsWith("µ", StringComparison.OrdinalIgnoreCase))
            multiplier = 1d / 1_000_000d;
        else if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            multiplier = 1d / 1_000d;
        if (multiplier != 1d)
            text = text[..^1];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number * multiplier
            : 0;
    }

    private static double ParseBytes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        var text = value.Trim();
        var units = new (string Suffix, double Multiplier)[]
        {
            ("Ki", 1024d), ("Mi", 1024d * 1024), ("Gi", 1024d * 1024 * 1024),
            ("Ti", 1024d * 1024 * 1024 * 1024), ("Pi", 1024d * 1024 * 1024 * 1024 * 1024),
            ("K", 1000d), ("M", 1000d * 1000), ("G", 1000d * 1000 * 1000),
            ("T", 1000d * 1000 * 1000 * 1000), ("P", 1000d * 1000 * 1000 * 1000 * 1000)
        };
        var multiplier = 1d;
        foreach (var unit in units)
        {
            if (!text.EndsWith(unit.Suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            multiplier = unit.Multiplier;
            text = text[..^unit.Suffix.Length];
            break;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number * multiplier
            : 0;
    }

    private static string FormatCpu(double cores)
        => cores > 0 ? $"{cores:0.###} 核" : string.Empty;

    private static string FormatBytes(double bytes)
    {
        if (bytes <= 0)
            return string.Empty;
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
        var value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
    private void BuildHealth(KubernetesDashboard dashboard)
    {
        var issues = new List<KubernetesHealthIssue>();

        foreach (var node in dashboard.Nodes.Where(node => node.Status != "Ready"))
            issues.Add(new KubernetesHealthIssue
            {
                Severity = "严重",
                Category = "节点",
                Namespace = "—",
                Object = node.Name,
                Message = $"节点状态 {node.Status}"
            });

        foreach (var pod in dashboard.Pods)
        {
            var background = pod.Containers.Count == 0 ? string.Empty : string.Join(", ", pod.Containers);
            if (IsProblemPodStatus(pod.Status))
                issues.Add(new KubernetesHealthIssue
                {
                    Severity = "严重",
                    Category = "Pod",
                    Namespace = pod.Namespace,
                    Object = pod.Name,
                    Message = $"状态 {pod.Status}，重启 {pod.Restarts} 次{DescribeContainers(background)}"
                });
            else if (pod.Restarts >= 5)
                issues.Add(new KubernetesHealthIssue
                {
                    Severity = "警告",
                    Category = "Pod",
                    Namespace = pod.Namespace,
                    Object = pod.Name,
                    Message = $"重启次数较高（{pod.Restarts}）{DescribeContainers(background)}"
                });
        }

        foreach (var workload in dashboard.Workloads.Where(workload =>
                     workload.Status is "进度中" or "失败" || workload.Status.Contains("失败", StringComparison.Ordinal)))
            issues.Add(new KubernetesHealthIssue
            {
                Severity = "警告",
                Category = "工作负载",
                Namespace = workload.Namespace,
                Object = $"{workload.Kind}/{workload.Name}",
                Message = $"状态 {workload.Status}，就绪 {workload.Ready}"
            });

        foreach (var pvc in dashboard.Pvcs.Where(pvc => pvc.Status is not ("Bound" or "—")))
            issues.Add(new KubernetesHealthIssue
            {
                Severity = "警告",
                Category = "存储",
                Namespace = pvc.Namespace,
                Object = pvc.Name,
                Message = $"PVC 状态 {pvc.Status}，StorageClass {pvc.StorageClass}"
            });

        foreach (var evt in dashboard.Events.Where(evt => evt.Type == "Warning"))
            issues.Add(new KubernetesHealthIssue
            {
                Severity = "警告",
                Category = "事件",
                Namespace = evt.Namespace,
                Object = evt.Object,
                Message = $"{evt.Reason}：{evt.Message}"
            });

        var ordered = issues
            .OrderBy(item => item.Severity == "严重" ? 0 : 1)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Object, StringComparer.OrdinalIgnoreCase)
            .ToList();
        HealthIssues.ReplaceRange(ordered);

        OnPropertyChanged(nameof(HasHealthIssues));
        OnPropertyChanged(nameof(HealthIssueCount));
        OnPropertyChanged(nameof(HealthCriticalCount));
        OnPropertyChanged(nameof(HealthWarningCount));
        OnPropertyChanged(nameof(HealthNodeIssueCount));
        OnPropertyChanged(nameof(HealthPodIssueCount));
        OnPropertyChanged(nameof(HealthWorkloadIssueCount));
        OnPropertyChanged(nameof(HealthStorageIssueCount));
        OnPropertyChanged(nameof(HealthStatusText));
    }

    private static bool IsProblemPodStatus(string status)
        => status is "Pending" or "Failed" or "CrashLoopBackOff" or "ImagePullBackOff" or
            "ErrImagePull" or "Evicted" or "Error" or "CreateContainerConfigError" or
            "ContainerCreating" or "Unknown";

    private static string DescribeContainers(string containers)
        => string.IsNullOrWhiteSpace(containers) ? string.Empty : $"（{containers}）";
    private async Task ScaleWorkloadAsync()
    {
        var connection = SelectedConnection;
        var workload = SelectedWorkload;
        if (connection is null || workload is null || connection.ReadOnly)
            return;

        var input = TextInputDialog.Prompt(
            _owner, "扩缩容",
            $"输入 {workload.Kind}/{workload.Name} 的新副本数（当前 {workload.DesiredReplicas}）：",
            workload.DesiredReplicas.ToString(CultureInfo.InvariantCulture));
        if (input is null)
            return;
        if (!int.TryParse(input.Trim(), out var replicas) || replicas < 0 || replicas > 1000)
        {
            ErrorMessage = "副本数必须是 0～1000 之间的整数。";
            return;
        }

        var server = FindSshServer(connection);
        var resource = workload.Kind == "StatefulSet" ? "statefulsets" : "deployments";
        if (!await EnsurePermissionAsync(connection, server, "patch", resource, workload.Namespace))
            return;
        if (!ConfirmWrite("扩缩容", $"{workload.Kind}/{workload.Name}",
                $"{workload.Namespace}：{workload.DesiredReplicas} → {replicas} 副本"))
            return;

        await ExecuteWriteAsync("k8s_scale", $"{workload.Namespace}/{workload.Kind}/{workload.Name} replicas={replicas}",
            () => _service.ScaleWorkloadAsync(connection, server, workload, replicas));
    }

    private async Task RestartWorkloadAsync()
    {
        var connection = SelectedConnection;
        var workload = SelectedWorkload;
        if (connection is null || workload is null || connection.ReadOnly)
            return;

        var server = FindSshServer(connection);
        var resource = workload.Kind switch
        {
            "StatefulSet" => "statefulsets",
            "DaemonSet" => "daemonsets",
            _ => "deployments"
        };
        if (!await EnsurePermissionAsync(connection, server, "patch", resource, workload.Namespace))
            return;
        if (!ConfirmWrite("重启", $"{workload.Kind}/{workload.Name}",
                $"{workload.Namespace}：触发滚动重启"))
            return;

        await ExecuteWriteAsync("k8s_rollout_restart", $"{workload.Namespace}/{workload.Kind}/{workload.Name}",
            () => _service.RestartWorkloadAsync(connection, server, workload));
    }

    private async Task RollbackWorkloadAsync()
    {
        var connection = SelectedConnection;
        var workload = SelectedWorkload;
        if (connection is null || workload is null || connection.ReadOnly)
            return;

        var server = FindSshServer(connection);
        if (!await EnsurePermissionAsync(connection, server, "patch", "deployments", workload.Namespace))
            return;
        if (!ConfirmWrite("回滚", $"Deployment/{workload.Name}",
                $"{workload.Namespace}：回滚到上一个 Deployment revision"))
            return;

        await ExecuteWriteAsync("k8s_rollout_undo", $"{workload.Namespace}/Deployment/{workload.Name}",
            () => _service.RollbackWorkloadAsync(connection, server, workload));
    }

    private async Task DeleteSelectedPodAsync()
    {
        var connection = SelectedConnection;
        var pod = SelectedPod;
        if (connection is null || pod is null || connection.ReadOnly)
            return;

        var server = FindSshServer(connection);
        if (!await EnsurePermissionAsync(connection, server, "delete", "pods", pod.Namespace))
            return;
        if (!ConfirmWrite("删除 Pod", $"{pod.Namespace}/{pod.Name}",
                $"{pod.Namespace}/{pod.Name}：Pod 删除后由控制器重建"))
            return;

        await ExecuteWriteAsync("k8s_delete_pod", $"{pod.Namespace}/{pod.Name}",
            () => _service.DeletePodAsync(connection, server, pod));
    }

    private async Task CordonSelectedNodeAsync(bool cordon)
    {
        var connection = SelectedConnection;
        var node = SelectedNode;
        if (connection is null || node is null || connection.ReadOnly)
            return;

        var server = FindSshServer(connection);
        if (!await EnsurePermissionAsync(connection, server, "update", "nodes", null))
            return;
        if (!ConfirmWrite(cordon ? "Cordon 节点" : "Uncordon 节点", node.Name,
                cordon ? "禁止新的 Pod 调度到该节点" : "恢复该节点调度"))
            return;

        await ExecuteWriteAsync(cordon ? "k8s_cordon" : "k8s_uncordon", node.Name,
            () => _service.CordonNodeAsync(connection, server, node.Name, cordon));
    }

    private async Task<bool> EnsurePermissionAsync(
        KubernetesConnectionConfig connection,
        SshServerConfig? server,
        string verb,
        string resource,
        string? namespaceName)
    {
        if (await _service.CanIAsync(connection, server, verb, resource, namespaceName, _lifetime.Token))
            return true;
        ErrorMessage = $"当前连接没有权限执行 {verb} {resource}" +
                       (string.IsNullOrWhiteSpace(namespaceName) ? "。" : $"（namespace={namespaceName}）。");
        return false;
    }

    private bool ConfirmWrite(string action, string target, string detail)
    {
        if (SelectedConnection is null)
            return false;

        var message = $"即将对 {SelectedConnection.DisplayName} 执行「{action}」。{Environment.NewLine}{Environment.NewLine}" +
                      $"目标：{target}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}" +
                      "此操作会写入集群，是否继续？";
        if (!_dialogs.Confirm(message, $"确认{action}"))
            return false;

        if (SelectedConnection.IsHighRisk)
        {
            return _dialogs.Confirm(
                $"这是生产环境写操作：{SelectedConnection.DisplayName}{Environment.NewLine}" +
                $"目标：{target}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}" +
                "请再次确认。",
                "生产环境二次确认");
        }
        return true;
    }

    private async Task ExecuteWriteAsync(string action, string target, Func<Task> execute)
    {
        IsWriteBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await execute();
            await AuditAsync(action, target, CommandStatus.Executed);
            await RefreshAsync();
            StatusMessage = $"{action} 已提交：{target}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{action} 失败：{ex.Message}";
            await AuditAsync(action, target, CommandStatus.Failed);
        }
        finally
        {
            IsWriteBusy = false;
        }
    }

    private async Task AuditAsync(string action, string result, CommandStatus status)
    {
        if (_audit is null || SelectedConnection is null)
            return;
        try
        {
            await _audit.InitializeAsync();
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = $"kubernetes:{SelectedConnection.Id}",
                ServerName = SelectedConnection.DisplayName,
                Command = action,
                Result = result,
                Status = status,
                Category = AuditCategory.Exec,
                Tool = "kubernetes",
                UserId = AuditContext.CurrentUserId
            });
        }
        catch
        {
        }
    }
    private void AddConnection()
    {
        var window = _windows.CreateKubernetesConnectionWindow(_owner);
        var vm = new KubernetesConnectionEditViewModel(_configService, _sshService, dialogs: _dialogs);
        window.DataContext = vm;
        vm.DialogClosed += (_, ok) =>
        {
            window.Close();
            if (ok)
                LoadAutoRefreshPrefs();
        _ = LoadConnectionsAsync();
        };
        window.ShowDialog();
    }

    private void EditConnection()
    {
        if (SelectedConnection is null)
            return;

        var window = _windows.CreateKubernetesConnectionWindow(_owner);
        var vm = new KubernetesConnectionEditViewModel(_configService, _sshService, SelectedConnection, _dialogs);
        window.DataContext = vm;
        vm.DialogClosed += (_, ok) =>
        {
            window.Close();
            if (ok)
                LoadAutoRefreshPrefs();
        _ = LoadConnectionsAsync();
        };
        window.ShowDialog();
    }

    private async Task DeleteConnectionAsync()
    {
        if (SelectedConnection is null)
            return;

        if (!_dialogs.Confirm(
                $"确认删除 K8s 连接 \"{SelectedConnection.DisplayName}\"？",
                "删除 K8s 连接"))
            return;

        try
        {
            var config = await _configService.LoadConfigAsync().ConfigureAwait(true);
            config.Kubernetes ??= new KubernetesConfig();
            config.Kubernetes.Connections = (config.Kubernetes.Connections ?? Array.Empty<KubernetesConnectionConfig>())
                .Where(item => item.Id != SelectedConnection.Id)
                .ToArray();
            if (config.Kubernetes.DefaultConnectionId == SelectedConnection.Id)
                config.Kubernetes.DefaultConnectionId = config.Kubernetes.Connections.FirstOrDefault()?.Id ?? string.Empty;
            await _configService.SaveConfigAsync(config).ConfigureAwait(true);
            await LoadConnectionsAsync().ConfigureAwait(true);
            StatusMessage = "已删除 K8s 连接。";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"删除 K8s 连接失败：{ex.Message}";
        }
    }

    private SshServerConfig? FindSshServer(KubernetesConnectionConfig connection)
    {
        if (!connection.IsRemote || string.IsNullOrWhiteSpace(connection.RemoteSshServerId))
            return null;
        return _sshServers.FirstOrDefault(item => item.Id == connection.RemoteSshServerId && !item.Disabled);
    }

    private void RebuildNamespaceFilters()
    {
        var filters = new List<string> { "全部命名空间" };
        filters.AddRange(Namespaces.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
        NamespaceFilters.ReplaceRange(filters);

        if (!NamespaceFilters.Contains(SelectedNamespace))
            _selectedNamespace = "全部命名空间";
        OnPropertyChanged(nameof(SelectedNamespace));
    }

    private void ApplyFilters()
    {
        ApplyWorkloadFilter();
        ApplyPodFilter();
        ApplyEventFilter();
        ApplyServiceFilter();
        ApplyIngressFilter();
        ApplyPvcFilter();
    }

    private void ApplyWorkloadFilter()
    {
        Workloads.ReplaceRange(_allWorkloads.Where(MatchesNamespace));
        OnPropertyChanged(nameof(HasWorkloads));
    }

    private void ApplyPodFilter()
    {
        var search = PodSearchText.Trim();
        Pods.ReplaceRange(_allPods.Where(MatchesNamespace).Where(item =>
            search.Length == 0 ||
            item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.Status.Contains(search, StringComparison.OrdinalIgnoreCase)));
        OnPropertyChanged(nameof(HasPods));
    }

    private void ApplyEventFilter()
    {
        Events.ReplaceRange(_allEvents.Where(MatchesNamespace));
        OnPropertyChanged(nameof(HasEvents));
    }

    private void ApplyServiceFilter()
    {
        Services.ReplaceRange(_allServices.Where(MatchesNamespace));
        OnPropertyChanged(nameof(HasServices));
    }

    private void ApplyIngressFilter()
    {
        Ingresses.ReplaceRange(_allIngresses.Where(MatchesNamespace));
        OnPropertyChanged(nameof(HasIngresses));
    }

    private void ApplyPvcFilter()
    {
        Pvcs.ReplaceRange(_allPvcs.Where(MatchesNamespace));
        OnPropertyChanged(nameof(HasPvcs));
    }

    private bool MatchesNamespace<K>(K item)
    {
        if (SelectedNamespace == "全部命名空间")
            return true;
        var namespaceName = item switch
        {
            KubernetesWorkloadInfo workload => workload.Namespace,
            KubernetesPodInfo pod => pod.Namespace,
            KubernetesEventInfo evt => evt.Namespace,
            KubernetesServiceInfo service => service.Namespace,
            KubernetesIngressInfo ingress => ingress.Namespace,
            KubernetesPvcInfo pvc => pvc.Namespace,
            _ => string.Empty
        };
        return string.Equals(namespaceName, SelectedNamespace, StringComparison.Ordinal);
    }

    private void ClearData()
    {
        Version = "—";
        Distribution = string.Empty;
        _metricsAvailable = false;
        _metricsStatus = "metrics-server 未检测";
        OnPropertyChanged(nameof(MetricsAvailable));
        OnPropertyChanged(nameof(MetricsStatus));
        NodeCount = ReadyNodeCount = NamespaceCount = PodCount = RunningPodCount = WarningEventCount = 0;
        Nodes.Clear();
        Namespaces.Clear();
        Workloads.Clear();
        Pods.Clear();
        Events.Clear();
        Services.Clear();
        Ingresses.Clear();
        Pvcs.Clear();
        StorageClasses.Clear();
        NamespaceFilters.Clear();
        _allWorkloads.Clear();
        _allPods.Clear();
        _allEvents.Clear();
        _allServices.Clear();
        _allIngresses.Clear();
        _allPvcs.Clear();
        _selectedNamespace = "全部命名空间";
        SelectedPod = null;
        SelectedWorkload = null;
        SelectedService = null;
        RelatedPods.Clear();
        RelatedServices.Clear();
        RelatedIngresses.Clear();
        PodContainers.Clear();
        PodRelatedServices.Clear();
        PodRelatedIngresses.Clear();
        PodEvents.Clear();
        ServiceRelatedPods.Clear();
        ServiceRelatedIngresses.Clear();
        HealthIssues.Clear();
        _allComparisonRows.Clear();
        _allIngressComparisonRows.Clear();
        ApplyComparisonFilter();
        WorkloadYaml = string.Empty;
        ServiceYaml = string.Empty;
        OnPropertyChanged(nameof(SelectedNamespace));
        OnPropertyChanged(nameof(NodeHealth));
        OnPropertyChanged(nameof(PodHealth));
        OnPropertyChanged(nameof(WarningHealth));
        OnPropertyChanged(nameof(HasNodes));
        OnPropertyChanged(nameof(HasWorkloads));
        OnPropertyChanged(nameof(HasPods));
        OnPropertyChanged(nameof(HasEvents));
        OnPropertyChanged(nameof(HasServices));
        OnPropertyChanged(nameof(HasIngresses));
        OnPropertyChanged(nameof(HasPvcs));
        OnPropertyChanged(nameof(HasStorageClasses));
        OnPropertyChanged(nameof(HasHealthIssues));
        OnPropertyChanged(nameof(HealthIssueCount));
        OnPropertyChanged(nameof(HealthCriticalCount));
        OnPropertyChanged(nameof(HealthWarningCount));
        OnPropertyChanged(nameof(HealthNodeIssueCount));
        OnPropertyChanged(nameof(HealthPodIssueCount));
        OnPropertyChanged(nameof(HealthWorkloadIssueCount));
        OnPropertyChanged(nameof(HealthStorageIssueCount));
        OnPropertyChanged(nameof(HealthStatusText));
    }

    private static void Replace<T>(RangeObservableCollection<T> target, IEnumerable<T> values)
        => target.ReplaceRange(values);

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedConnection));
        OnPropertyChanged(nameof(HasConnection));
        OnPropertyChanged(nameof(HasNoConnection));
        OnPropertyChanged(nameof(ConnectionEnvironment));
        OnPropertyChanged(nameof(ConnectionProvider));
        OnPropertyChanged(nameof(ConnectionAccess));
        OnPropertyChanged(nameof(ConnectionContext));
        OnPropertyChanged(nameof(ConnectionNamespace));
        OnPropertyChanged(nameof(IsHighRiskConnection));
    }

    private void RaiseCommandStates()
    {
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (EditConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshPodDetailCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshWorkloadDetailCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshPodLogsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshServiceDetailCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CompareReleasesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ScaleWorkloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestartWorkloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RollbackWorkloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeletePodCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CordonNodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UncordonNodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void LoadAutoRefreshPrefs()
    {
        var enabled = bool.TryParse(UiPrefs.GetString("kubernetes.autoRefresh", "false"), out var enabledValue) && enabledValue;
        var seconds = int.TryParse(UiPrefs.GetString("kubernetes.autoRefreshSeconds", "30"), out var secondsValue)
            ? secondsValue
            : 30;
        _autoRefreshSeconds = AutoRefreshOptions.Contains(seconds) ? seconds : 30;
        OnPropertyChanged(nameof(AutoRefreshSeconds));
        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(_autoRefreshSeconds);
        _isAutoRefreshEnabled = enabled;
        OnPropertyChanged(nameof(IsAutoRefreshEnabled));
        if (enabled && _isActive)
            _autoRefreshTimer.Start();
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _autoRefreshTimer.Stop();
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private SshServerConfig[] _sshServers = Array.Empty<SshServerConfig>();

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
