using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.ViewModels;

public class DatasourceEditViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly DatasourceEditWindow _editWindow;
    private readonly DataSourceConfig? _editingDs;
    private readonly SshServerConfig[] _servers;
    private readonly IUserDialogService _dialogs;
    private readonly bool _viewOnly;

    private string _name = string.Empty;
    private int _typeIndex;
    private string _host = string.Empty;
    private string _portText = "3306";
    private string _username = string.Empty;
    private string _defaultDatabase = string.Empty;
    private string _description = string.Empty;
    private string _group = string.Empty;
    private string _tagsText = string.Empty;
    private string _assetKey = string.Empty;
    private string _instanceKey = string.Empty;
    private string _aliasesText = string.Empty;
    private int _runtimeKindIndex;
    private string _selectedSshAssetKey = string.Empty;
    private string _selectedDockerAssetKey = string.Empty;
    private string _runtimeContainerName = string.Empty;
    private string _runtimeComposeService = string.Empty;
    private int _accessModeIndex;
    private int _tunnelServerIndex;
    private string _statusMessage = string.Empty;
    private bool _isTestRunning;
    private bool _readOnlyMode;
    private bool _usePooling;    private string _maxRowsText = string.Empty;
    private string _timeoutText = string.Empty;
    private int _writeApprovalIndex;
    private int _sslModeIndex = 1;
    private bool _allowPublicKeyRetrieval;
    private string _sslCaFile = string.Empty;

    public event EventHandler<bool>? DialogClosed;

    public DatasourceEditViewModel(
        IConfigService configService,
        DatasourceEditWindow editWindow,
        SshServerConfig[] servers,
        DataSourceConfig? datasource = null,
        DataSourceConfig? prefill = null,
        IUserDialogService? dialogs = null,
        bool viewOnly = false)
    {
        _configService = configService;
        _editWindow = editWindow;
        _editingDs = datasource;
        _servers = servers ?? Array.Empty<SshServerConfig>();
        _dialogs = dialogs ?? new UserDialogService();
        _viewOnly = viewOnly;
        TunnelServerOptions = new[] { "(自动, 按关系推导)" }
            .Concat(_servers.Select(s => $"{s.Name} ({s.Host})"))
            .ToArray();

        SaveCommand = new RelayCommand(_ => Save(), _ => !_viewOnly);
        CancelCommand = new RelayCommand(_ => Cancel());
        TestConnectionCommand = new RelayCommand(_ => TestConnection(), _ => !_isTestRunning);

        if (datasource != null)
        {
            Name = datasource.Name;
            TypeIndex = datasource.Type.ToLowerInvariant() switch { "redis" => 1, "postgres" => 2, _ => 0 };
            Host = datasource.Host;
            PortText = datasource.Port.ToString();
            Username = datasource.Username;
            DefaultDatabase = datasource.DefaultDatabase ?? string.Empty;
            Description = datasource.Description ?? string.Empty;
            Group = datasource.Group ?? string.Empty;
            TagsText = string.Join(", ", datasource.Tags);
            AssetKey = datasource.AssetKey ?? string.Empty;
            InstanceKey = datasource.InstanceKey ?? string.Empty;
            AliasesText = string.Join(", ", datasource.Aliases);
            RuntimeKindIndex = (int)datasource.RuntimeKind;
            SelectedSshAssetKey = datasource.RuntimeSshAssetKey ?? string.Empty;
            SelectedDockerAssetKey = datasource.RuntimeDockerAssetKey ?? string.Empty;
            RuntimeContainerName = datasource.RuntimeContainerName ?? string.Empty;
            RuntimeComposeService = datasource.RuntimeComposeService ?? string.Empty;
            AccessModeIndex = datasource.AccessMode == AccessMode.SshTunnel ? 1 : 0;
            TunnelServerIndex = ResolveTunnelIndex(datasource.TunnelServerId);
            ReadOnlyMode = datasource.ReadOnly;
            MaxRowsText = datasource.MaxRows?.ToString() ?? string.Empty;
            TimeoutText = datasource.TimeoutSeconds?.ToString() ?? string.Empty;
            WriteApprovalIndex = datasource.WriteApproval == WriteApprovalMode.AutoApprove ? 1 : 0;
            UsePooling = datasource.UsePooling;
            SslModeIndex = (int)datasource.SslMode;
            AllowPublicKeyRetrieval = datasource.AllowPublicKeyRetrieval;
            SslCaFile = datasource.SslCaFile ?? string.Empty;
        }
        else if (prefill != null)
        {
            Name = string.IsNullOrWhiteSpace(prefill.Name) ? prefill.Host : prefill.Name;
            TypeIndex = prefill.Type.ToLowerInvariant() switch { "redis" => 1, "postgres" => 2, _ => 0 };
            Host = prefill.Host;
            PortText = prefill.Port.ToString();
        }

        if (_viewOnly)
            _statusMessage = "只读查看：不能修改或保存配置。";

        _ = LoadRuntimeOptionsAsync();
    }

    public bool CanEdit => !_viewOnly;
    public string CancelButtonText => _viewOnly ? "关闭" : "取消";

    public string[] TunnelServerOptions { get; }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public int TypeIndex
    {
        get => _typeIndex;
        set
        {
            if (_typeIndex == value) return;
            var previousDefault = DefaultPort(_typeIndex);
            _typeIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SupportsSsl));
            OnPropertyChanged(nameof(IsMySql));

            // 切换类型时, 如果端口还是另一类型的默认值, 自动带出本类型默认端口
            if (string.IsNullOrWhiteSpace(PortText) || PortText.Trim() == previousDefault)
                PortText = DefaultPort(value);
        }
    }

    private static string DefaultPort(int typeIndex) => typeIndex switch
    {
        1 => "6379",   // Redis
        2 => "5432",   // PostgreSQL
        _ => "3306"    // MySQL
    };

    public string Host
    {
        get => _host;
        set { _host = value; OnPropertyChanged(); }
    }

    public string PortText
    {
        get => _portText;
        set { _portText = value; OnPropertyChanged(); }
    }

    public string Username
    {
        get => _username;
        set { _username = value; OnPropertyChanged(); }
    }

    public string DefaultDatabase
    {
        get => _defaultDatabase;
        set { _defaultDatabase = value; OnPropertyChanged(); }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    public string Group
    {
        get => _group;
        set { _group = value; OnPropertyChanged(); }
    }

    public string TagsText
    {
        get => _tagsText;
        set { _tagsText = value; OnPropertyChanged(); }
    }

    public string AssetKey
    {
        get => _assetKey;
        set { _assetKey = value; OnPropertyChanged(); }
    }

    public string InstanceKey
    {
        get => _instanceKey;
        set { _instanceKey = value; OnPropertyChanged(); }
    }

    public string AliasesText
    {
        get => _aliasesText;
        set { _aliasesText = value; OnPropertyChanged(); }
    }

    public ObservableCollection<RuntimeTargetOption> SshTargetOptions { get; } = new();
    public ObservableCollection<RuntimeTargetOption> DockerTargetOptions { get; } = new();

    public string[] RuntimeKindOptions { get; } = { "未配置", "SSH 服务器", "Docker 应用", "独立环境" };

    public int RuntimeKindIndex
    {
        get => _runtimeKindIndex;
        set
        {
            if (_runtimeKindIndex == value) return;
            _runtimeKindIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSshRuntime));
            OnPropertyChanged(nameof(IsDockerRuntime));
        }
    }

    public bool IsSshRuntime => RuntimeKindIndex == 1;
    public bool IsDockerRuntime => RuntimeKindIndex == 2;

    public string SelectedSshAssetKey
    {
        get => _selectedSshAssetKey;
        set { _selectedSshAssetKey = value; OnPropertyChanged(); }
    }

    public string SelectedDockerAssetKey
    {
        get => _selectedDockerAssetKey;
        set { _selectedDockerAssetKey = value; OnPropertyChanged(); }
    }

    public string RuntimeContainerName
    {
        get => _runtimeContainerName;
        set { _runtimeContainerName = value; OnPropertyChanged(); }
    }

    public string RuntimeComposeService
    {
        get => _runtimeComposeService;
        set { _runtimeComposeService = value; OnPropertyChanged(); }
    }

    public int AccessModeIndex
    {
        get => _accessModeIndex;
        set { _accessModeIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsTunnelMode)); }
    }

    public bool IsTunnelMode => AccessModeIndex == 1;

    public int TunnelServerIndex
    {
        get => _tunnelServerIndex;
        set { _tunnelServerIndex = value; OnPropertyChanged(); }
    }

    // —— 治理（可选）——
    public bool ReadOnlyMode
    {
        get => _readOnlyMode;
        set { _readOnlyMode = value; OnPropertyChanged(); }
    }

    /// <summary>启用连接池（仅直连；经 SSH 隧道时无效）。</summary>
    public bool UsePooling
    {
        get => _usePooling;
        set { _usePooling = value; OnPropertyChanged(); }
    }

    public string MaxRowsText
    {
        get => _maxRowsText;
        set { _maxRowsText = value; OnPropertyChanged(); }
    }

    public string TimeoutText
    {
        get => _timeoutText;
        set { _timeoutText = value; OnPropertyChanged(); }
    }

    public int WriteApprovalIndex
    {
        get => _writeApprovalIndex;
        set { _writeApprovalIndex = value; OnPropertyChanged(); }
    }

    public string[] WriteApprovalOptions { get; } = { "一律桌面确认", "自动放行(受信任)" };

    // —— TLS（MySQL/PostgreSQL；Redis 不适用）——

    public bool SupportsSsl => TypeIndex != 1;

    public bool IsMySql => TypeIndex == 0;

    public int SslModeIndex
    {
        get => _sslModeIndex;
        set { _sslModeIndex = value; OnPropertyChanged(); }
    }

    public string[] SslModeOptions { get; } =
    {
        "禁用 (None)", "优先 (Preferred)", "必须 (Required)", "校验证书链 (VerifyCA)", "校验主机名 (VerifyFull)"
    };

    /// <summary>MySQL 8 caching_sha2_password 在非 TLS 下允许获取服务端公钥。</summary>
    public bool AllowPublicKeyRetrieval
    {
        get => _allowPublicKeyRetrieval;
        set { _allowPublicKeyRetrieval = value; OnPropertyChanged(); }
    }

    /// <summary>自定义 CA 证书（本地 PEM 路径），配合 VerifyCA/VerifyFull。</summary>
    public string SslCaFile
    {
        get => _sslCaFile;
        set { _sslCaFile = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand TestConnectionCommand { get; }

    private int ResolveTunnelIndex(string? serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return 0;
        var index = Array.FindIndex(_servers, s => s.Id == serverId);
        return index < 0 ? 0 : index + 1;
    }

    private async Task LoadRuntimeOptionsAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            SshTargetOptions.Clear();
            DockerTargetOptions.Clear();

            var seenServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var server in config.Servers
                         .Where(s => !string.IsNullOrWhiteSpace(s.AssetKey))
                         .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (seenServers.Add(server.AssetKey!))
                    SshTargetOptions.Add(new RuntimeTargetOption(server.AssetKey!, $"{server.Name} ({server.AssetKey})"));
            }

            var seenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in config.Applications
                         .Where(a => !string.IsNullOrWhiteSpace(a.AssetKey) &&
                                     string.Equals(a.Type, "docker", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (seenApps.Add(app.AssetKey!))
                    DockerTargetOptions.Add(new RuntimeTargetOption(app.AssetKey!, $"{app.Name} ({app.AssetKey})"));
            }
        }
        catch
        {
            // 选项加载失败不阻塞编辑；保存时仍会做运行环境校验
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void TestConnection() => AsyncOperation.Run("测试数据源连接", async () =>
    {
        _isTestRunning = true;
        StatusMessage = "测试连接中...";

        try
        {
            var ds = BuildDataSource();
            ds.Password = _editWindow.GetPassword();
            if (string.IsNullOrEmpty(ds.Password)) ds.Password = _editingDs?.Password;

            var securityOptions = new LitSSHmcp.Core.Services.Security.SecurityOptionsProvider();
            var knownHosts = new LitSSHmcp.Core.Services.SSH.FileSshKnownHostsStore();
            var targetLimiter = new LitSSHmcp.Core.Services.Security.TargetLimiter(securityOptions, ConfigPaths.TargetStateDir);
            var mysql = new MySqlConnectionProvider(_configService, knownHosts, securityOptions, targetLimiter);
            var postgres = new PostgresConnectionProvider(_configService, knownHosts, securityOptions, targetLimiter);
            var redis = new RedisConnectionProvider(_configService, knownHosts, securityOptions, targetLimiter);
            var registry = new DatasourceDriverRegistry(new IDatasourceDriver[] { new MySqlDriver(mysql), new PostgresDriver(postgres), new RedisDriver(redis) });
            var driver = registry.Get(ds.Type);
            if (driver == null)
            {
                StatusMessage = $"暂不支持的类型: {ds.Type}";
                return;
            }

            var result = await driver.TestAsync(ds);
            StatusMessage = result.Success
                ? $"连接成功 ({result.DurationMs:F0}ms) {result.Version}"
                : $"连接失败: {result.Error}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接失败: {ex.Message}";
        }
        finally
        {
            _isTestRunning = false;
        }
    });

    private void Save() => AsyncOperation.Run("保存数据源", async () =>
    {
        if (_viewOnly)
            return;
        try
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Host))
            {
                StatusMessage = "名称和主机地址不能为空";
                return;
            }

            var config = await _configService.LoadConfigAsync();
            var ds = BuildDataSource();
            var password = _editWindow.GetPassword();
            ds.Password = string.IsNullOrEmpty(password) ? _editingDs?.Password : password;

            var duplicates = DuplicateAssetDetector.FindDatasourceMatches(config, ds);
            if (duplicates.Count > 0 &&
                !_dialogs.Confirm(BuildDuplicateMessage(ds, duplicates), "疑似重复的数据源配置"))
            {
                return;
            }

            if (_editingDs != null)
            {
                ds.Id = _editingDs.Id;
                ds.CreatedAt = _editingDs.CreatedAt;
                var index = Array.FindIndex(config.DataSources, d => d.Id == _editingDs.Id);
                if (index >= 0)
                    config.DataSources[index] = ds;
            }
            else
            {
                config.DataSources = config.DataSources.Append(ds).ToArray();
            }

            var errors = AssetRuntimeValidator.Validate(config).Issues
                .Where(i => i.AssetId == ds.Id && i.Severity == AssetRuntimeIssueSeverity.Error)
                .ToArray();
            if (errors.Length > 0)
            {
                StatusMessage = string.Join(Environment.NewLine,
                    errors.Take(4).Select(i => i.Message));
                return;
            }

            await _configService.SaveConfigAsync(config);
            _ = AssetIdentitySaveProbe.CaptureDatasourceAsync(_configService, ds);
            DialogClosed?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
    });

    private void Cancel()
    {
        DialogClosed?.Invoke(this, false);
    }

    private static string[] SplitAliases(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.Length > 0)
                .ToArray();

    private static string BuildDuplicateMessage(
        DataSourceConfig candidate, IReadOnlyList<DuplicateAssetItem> duplicates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"检测到与当前数据源类型/主机/端口重叠的现有配置（{duplicates.Count} 条）：");
        foreach (var item in duplicates.Take(8))
        {
            sb.AppendLine($"  - {item.Name} ({item.Id})  {item.Host}:{item.Port}");
            if (!string.IsNullOrWhiteSpace(item.Detail))
                sb.AppendLine($"    {item.Detail}");
        }
        if (duplicates.Count > 8)
            sb.AppendLine($"  … 另有 {duplicates.Count - 8} 条");

        sb.AppendLine();
        sb.AppendLine($"当前配置: {candidate.Name}  {candidate.Type} {candidate.Host}:{candidate.Port}");
        sb.AppendLine();
        sb.AppendLine("如果这是同一数据库实例的不同连接配置（直连/隧道/不同账号/不同库），可以继续保存；");
        sb.AppendLine("建议保留这些连接配置，后续由资产身份探测自动归并。");
        sb.AppendLine();
        sb.Append("确定继续保存吗？");
        return sb.ToString();
    }

    private DataSourceConfig BuildDataSource()
    {
        var tags = TagsText.Split(',')
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToArray();

        var port = int.TryParse(PortText, out var p) ? p : (TypeIndex == 1 ? 6379 : 3306);
        string? tunnelServerId = null;
        if (AccessModeIndex == 1 && TunnelServerIndex > 0 && TunnelServerIndex - 1 < _servers.Length)
            tunnelServerId = _servers[TunnelServerIndex - 1].Id;

        var runtimeKind = RuntimeKindIndex switch
        {
            1 => RuntimeEnvironmentKind.SshServer,
            2 => RuntimeEnvironmentKind.DockerApplication,
            3 => RuntimeEnvironmentKind.Standalone,
            _ => RuntimeEnvironmentKind.Unspecified
        };

        return new DataSourceConfig
        {
            Id = _editingDs?.Id ?? Guid.NewGuid().ToString("N"),
            Name = Name,
            Type = TypeIndex switch { 1 => "redis", 2 => "postgres", _ => "mysql" },
            Host = Host,
            Port = port,
            Username = Username,
            DefaultDatabase = string.IsNullOrWhiteSpace(DefaultDatabase) ? null : DefaultDatabase,
            AccessMode = AccessModeIndex == 1 ? AccessMode.SshTunnel : AccessMode.Direct,
            TunnelServerId = tunnelServerId,
            Group = string.IsNullOrWhiteSpace(Group) ? null : Group.Trim(),
            Description = Description,
            Tags = tags,
            Aliases = SplitAliases(AliasesText),
            AssetKey = string.IsNullOrWhiteSpace(AssetKey) ? null : AssetKey.Trim(),
            InstanceKey = string.IsNullOrWhiteSpace(InstanceKey) ? null : InstanceKey.Trim(),
            RuntimeKind = runtimeKind,
            RuntimeSshAssetKey = runtimeKind == RuntimeEnvironmentKind.SshServer
                ? Normalize(SelectedSshAssetKey)
                : null,
            RuntimeDockerAssetKey = runtimeKind == RuntimeEnvironmentKind.DockerApplication
                ? Normalize(SelectedDockerAssetKey)
                : null,
            RuntimeContainerName = runtimeKind == RuntimeEnvironmentKind.DockerApplication
                ? Normalize(RuntimeContainerName)
                : null,
            RuntimeComposeService = runtimeKind == RuntimeEnvironmentKind.DockerApplication
                ? Normalize(RuntimeComposeService)
                : null,
            ReadOnly = ReadOnlyMode,
            UsePooling = UsePooling,
            MaxRows = int.TryParse(MaxRowsText?.Trim(), out var maxRows) && maxRows > 0 ? maxRows : null,
            TimeoutSeconds = int.TryParse(TimeoutText?.Trim(), out var timeout) && timeout > 0 ? timeout : null,
            WriteApproval = WriteApprovalIndex == 1 ? WriteApprovalMode.AutoApprove : WriteApprovalMode.Always,
            SslMode = SslModeIndex is >= 0 and <= 4 ? (DataSourceSslMode)SslModeIndex : DataSourceSslMode.Preferred,
            AllowPublicKeyRetrieval = IsMySql && AllowPublicKeyRetrieval,
            SslCaFile = string.IsNullOrWhiteSpace(SslCaFile) ? null : SslCaFile.Trim()
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
