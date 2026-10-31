using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.ViewModels;

public class ApplicationEditViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly ApplicationConfig? _editing;

    private string _name = string.Empty;
    private string _type = "java";
    private string _host = string.Empty;
    private string _portText = string.Empty;
    private string _group = string.Empty;
    private string _description = string.Empty;
    private string _containerName = string.Empty;
    private string _appPath = string.Empty;
    private string _logPaths = string.Empty;
    private string _statusMessage = string.Empty;
    private string _assetKey = string.Empty;
    private string _aliasesText = string.Empty;
    private int _runtimeKindIndex;
    private string _selectedSshAssetKey = string.Empty;
    private string _selectedDockerAssetKey = string.Empty;
    private string _runtimeContainerName = string.Empty;
    private string _runtimeComposeService = string.Empty;

    public event EventHandler<bool>? DialogClosed;

    public ApplicationEditViewModel(IConfigService configService, ApplicationConfig? application = null, ApplicationConfig? prefill = null)
    {
        _configService = configService;
        _editing = application;

        SaveCommand = new RelayCommand(_ => _ = SaveAsync());
        CancelCommand = new RelayCommand(_ => DialogClosed?.Invoke(this, false));
        AddUrlCommand = new RelayCommand(_ => UrlRows.Add(new UrlPatternRow()));
        RemoveUrlCommand = new RelayCommand(parameter =>
        {
            if (parameter is UrlPatternRow row)
                UrlRows.Remove(row);
        });

        if (application != null)
        {
            Name = application.Name;
            Type = application.Type;
            Host = application.Host ?? string.Empty;
            PortText = application.Port?.ToString() ?? string.Empty;
            Group = application.Group ?? string.Empty;
            Description = application.Description ?? string.Empty;
            ContainerName = application.ContainerName ?? string.Empty;
            AppPath = application.Path ?? string.Empty;
            LogPaths = application.LogPaths == null ? string.Empty : string.Join(Environment.NewLine, application.LogPaths);
            AssetKey = application.AssetKey ?? string.Empty;
            AliasesText = string.Join(", ", application.Aliases);
            RuntimeKindIndex = (int)application.RuntimeKind;
            SelectedSshAssetKey = application.RuntimeSshAssetKey ?? string.Empty;
            SelectedDockerAssetKey = application.RuntimeDockerAssetKey ?? string.Empty;
            RuntimeContainerName = application.RuntimeContainerName ?? string.Empty;
            RuntimeComposeService = application.RuntimeComposeService ?? string.Empty;
            foreach (var url in application.Urls ?? Array.Empty<UrlPattern>())
                UrlRows.Add(UrlPatternRow.From(url));
        }
        else if (prefill != null)
        {
            // "待确认"节点确认时的新增预填（仍是新增模式，_editing 为 null）
            Name = prefill.Name;
            Type = string.IsNullOrWhiteSpace(prefill.Type) ? "java" : prefill.Type;
            Host = prefill.Host ?? string.Empty;
            PortText = prefill.Port?.ToString() ?? string.Empty;
            ContainerName = prefill.ContainerName ?? string.Empty;
            AppPath = prefill.Path ?? string.Empty;
            Description = prefill.Description ?? string.Empty;
        }

        _ = LoadRuntimeOptionsAsync();
    }

    public string WindowTitle => _editing == null ? "添加应用" : "编辑应用";

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public string PortText { get => _portText; set => Set(ref _portText, value); }
    public string Group { get => _group; set => Set(ref _group, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string ContainerName { get => _containerName; set => Set(ref _containerName, value); }
    public string AppPath { get => _appPath; set => Set(ref _appPath, value); }
    public string LogPaths { get => _logPaths; set => Set(ref _logPaths, value); }
    public string AssetKey { get => _assetKey; set => Set(ref _assetKey, value); }
    public string AliasesText { get => _aliasesText; set => Set(ref _aliasesText, value); }

    public ObservableCollection<RuntimeTargetOption> SshTargetOptions { get; } = new();
    public ObservableCollection<RuntimeTargetOption> DockerTargetOptions { get; } = new();
    public ObservableCollection<UrlPatternRow> UrlRows { get; } = new();

    public string[] RuntimeKindOptions { get; } = { "未配置", "SSH 服务器", "Docker 应用", "独立环境" };
    public string[] UrlRoleOptions { get; } = { "自动", "公网入口", "后端应用", "内部访问" };

    public int RuntimeKindIndex
    {
        get => _runtimeKindIndex;
        set
        {
            if (!Set(ref _runtimeKindIndex, value))
                return;
            OnPropertyChanged(nameof(IsSshRuntime));
            OnPropertyChanged(nameof(IsDockerRuntime));
        }
    }

    public bool IsSshRuntime => RuntimeKindIndex == 1;
    public bool IsDockerRuntime => RuntimeKindIndex == 2;

    public string SelectedSshAssetKey
    {
        get => _selectedSshAssetKey;
        set => Set(ref _selectedSshAssetKey, value);
    }

    public string SelectedDockerAssetKey
    {
        get => _selectedDockerAssetKey;
        set => Set(ref _selectedDockerAssetKey, value);
    }

    public string RuntimeContainerName
    {
        get => _runtimeContainerName;
        set => Set(ref _runtimeContainerName, value);
    }

    public string RuntimeComposeService
    {
        get => _runtimeComposeService;
        set => Set(ref _runtimeComposeService, value);
    }

    public ICommand AddUrlCommand { get; }
    public ICommand RemoveUrlCommand { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "应用名称不能为空";
            return;
        }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(PortText))
        {
            if (!int.TryParse(PortText.Trim(), out var value) || value < 1 || value > 65535)
            {
                StatusMessage = "端口必须是 1-65535 的数字";
                return;
            }
            port = value;
        }

        try
        {
            var config = await _configService.LoadConfigAsync();
            var app = BuildApplication(port);

            if (_editing == null)
            {
                config.Applications = config.Applications.Append(app).ToArray();
            }
            else
            {
                var index = Array.FindIndex(config.Applications, a => a.Id == _editing.Id);
                if (index < 0)
                {
                    StatusMessage = "应用不存在，可能已被删除";
                    return;
                }

                config.Applications[index] = app;
            }

            var errors = AssetRuntimeValidator.Validate(config).Issues
                .Where(i => i.AssetId == app.Id && i.Severity == AssetRuntimeIssueSeverity.Error)
                .ToArray();
            if (errors.Length > 0)
            {
                StatusMessage = string.Join(Environment.NewLine,
                    errors.Take(4).Select(i => i.Message));
                return;
            }

            await _configService.SaveConfigAsync(config);
            DialogClosed?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
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
                                     string.Equals(a.Type, "docker", StringComparison.OrdinalIgnoreCase) &&
                                     !string.Equals(a.Id, _editing?.Id, StringComparison.OrdinalIgnoreCase) &&
                                     !string.Equals(a.AssetKey, AssetKey, StringComparison.OrdinalIgnoreCase))
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

    private ApplicationConfig BuildApplication(int? port)
    {
        var runtimeKind = RuntimeKindIndex switch
        {
            1 => RuntimeEnvironmentKind.SshServer,
            2 => RuntimeEnvironmentKind.DockerApplication,
            3 => RuntimeEnvironmentKind.Standalone,
            _ => RuntimeEnvironmentKind.Unspecified
        };

        return new ApplicationConfig
        {
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = Name.Trim(),
            Type = string.IsNullOrWhiteSpace(Type) ? "java" : Type.Trim(),
            Host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim(),
            Port = port,
            Group = string.IsNullOrWhiteSpace(Group) ? null : Group.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            ContainerName = string.IsNullOrWhiteSpace(ContainerName) ? null : ContainerName.Trim(),
            Path = string.IsNullOrWhiteSpace(AppPath) ? null : AppPath.Trim(),
            LogPaths = SplitPaths(LogPaths),
            AssetKey = string.IsNullOrWhiteSpace(AssetKey) ? null : AssetKey.Trim(),
            Aliases = SplitAliases(AliasesText),
            Tags = _editing?.Tags ?? Array.Empty<string>(),
            ServerId = _editing?.ServerId,
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
            Urls = UrlRows
                .Select(row => row.ToModel())
                .Where(url => url is not null)
                .Select(url => url!)
                .ToArray()
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] SplitPaths(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.TrimEnd('\r').Trim())
                .Where(x => x.Length > 0)
                .ToArray();

    private static string[] SplitAliases(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.Length > 0)
                .ToArray();

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
