using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public class DatasourceEditViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly DatasourceEditWindow _editWindow;
    private readonly DataSourceConfig? _editingDs;
    private readonly SshServerConfig[] _servers;

    private string _name = string.Empty;
    private int _typeIndex;
    private string _host = string.Empty;
    private string _portText = "3306";
    private string _username = string.Empty;
    private string _defaultDatabase = string.Empty;
    private string _description = string.Empty;
    private string _tagsText = string.Empty;
    private int _accessModeIndex;
    private int _tunnelServerIndex;
    private string _statusMessage = string.Empty;
    private bool _isTestRunning;
    private bool _readOnlyMode;
    private string _maxRowsText = string.Empty;
    private string _timeoutText = string.Empty;
    private int _writeApprovalIndex;

    public event EventHandler<bool>? DialogClosed;

    public DatasourceEditViewModel(
        IConfigService configService,
        DatasourceEditWindow editWindow,
        SshServerConfig[] servers,
        DataSourceConfig? datasource = null)
    {
        _configService = configService;
        _editWindow = editWindow;
        _editingDs = datasource;
        _servers = servers ?? Array.Empty<SshServerConfig>();
        TunnelServerOptions = new[] { "(自动, 按关系推导)" }
            .Concat(_servers.Select(s => $"{s.Name} ({s.Host})"))
            .ToArray();

        SaveCommand = new RelayCommand(_ => Save());
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
            TagsText = string.Join(", ", datasource.Tags);
            AccessModeIndex = datasource.AccessMode == AccessMode.SshTunnel ? 1 : 0;
            TunnelServerIndex = ResolveTunnelIndex(datasource.TunnelServerId);
            ReadOnlyMode = datasource.ReadOnly;
            MaxRowsText = datasource.MaxRows?.ToString() ?? string.Empty;
            TimeoutText = datasource.TimeoutSeconds?.ToString() ?? string.Empty;
            WriteApprovalIndex = datasource.WriteApproval == WriteApprovalMode.AutoApprove ? 1 : 0;
        }
    }

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

    public string TagsText
    {
        get => _tagsText;
        set { _tagsText = value; OnPropertyChanged(); }
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

    private async void TestConnection()
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
            var targetLimiter = new LitSSHmcp.Core.Services.Security.TargetLimiter(securityOptions);
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
    }

    private async void Save()
    {
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

            await _configService.SaveConfigAsync(config);
            DialogClosed?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
    }

    private void Cancel()
    {
        DialogClosed?.Invoke(this, false);
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
            Description = Description,
            Tags = tags,
            ReadOnly = ReadOnlyMode,
            MaxRows = int.TryParse(MaxRowsText?.Trim(), out var maxRows) && maxRows > 0 ? maxRows : null,
            TimeoutSeconds = int.TryParse(TimeoutText?.Trim(), out var timeout) && timeout > 0 ? timeout : null,
            WriteApproval = WriteApprovalIndex == 1 ? WriteApprovalMode.AutoApprove : WriteApprovalMode.Always
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
