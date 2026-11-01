using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Jenkins;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.ViewModels;

/// <summary>环境类型只是展示标签；同一标签可有多个连接，不限制连接数量。</summary>
public sealed record JenkinsEnvironmentOption(string Kind, string Label);

public sealed class JenkinsConnectionEditViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly IUserDialogService _dialogs;
    private readonly JenkinsConnectionConfig? _editing;
    private readonly bool _isNew;
    private string _apiToken = string.Empty;
    private string _name = string.Empty;
    private string _environmentLabel = string.Empty;
    private string _baseUrl = string.Empty;
    private string _username = string.Empty;
    private string _description = string.Empty;
    private string _timeoutText = "30";
    private int _selectedEnvironmentIndex;
    private bool _readOnly;
    private bool _requireConfirmation = true;
    private bool _ignoreTlsErrors;
    private bool _isEnabled = true;
    private bool _useSshTunnel;
    private string _tunnelSshServerId = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;

    public JenkinsConnectionEditViewModel(
        IConfigService configService,
        JenkinsConnectionConfig? editing = null,
        IUserDialogService? dialogs = null)
    {
        _configService = configService;
        _dialogs = dialogs ?? new UserDialogService();
        _editing = editing;
        _isNew = editing is null;
        EnvironmentOptions =
        [
            new(JenkinsEnvironmentKinds.Test, "测试"),
            new(JenkinsEnvironmentKinds.Staging, "预发布"),
            new(JenkinsEnvironmentKinds.Production, "生产"),
            new(JenkinsEnvironmentKinds.Partner, "第三方"),
            new(JenkinsEnvironmentKinds.Custom, "自定义")
        ];

        if (editing is not null)
        {
            Name = editing.Name;
            BaseUrl = editing.BaseUrl;
            Username = editing.Username;
            Description = editing.Description;
            TimeoutText = editing.TimeoutSeconds > 0 ? editing.TimeoutSeconds.ToString() : "30";
            ReadOnly = editing.ReadOnly;
            RequireConfirmation = editing.RequireConfirmation;
            IgnoreTlsErrors = editing.IgnoreTlsErrors;
            IsEnabled = editing.Enabled;
            EnvironmentLabel = editing.EnvironmentLabel;
            _selectedEnvironmentIndex = FindEnvironmentIndex(editing.EnvironmentKind);
            UseSshTunnel = !string.IsNullOrWhiteSpace(editing.TunnelSshServerId);
            TunnelSshServerId = editing.TunnelSshServerId;
        }

        _ = LoadSshServersAsync();

        SaveCommand = new RelayCommand(_ => AsyncOperation.Run("保存 Jenkins 连接", SaveAsync), _ => !_isBusy);
        CancelCommand = new RelayCommand(_ => DialogClosed?.Invoke(this, false));
        TestConnectionCommand = new RelayCommand(_ => AsyncOperation.Run("测试 Jenkins 连接", TestConnectionAsync), _ => !_isBusy);
        DeleteCommand = new RelayCommand(_ => AsyncOperation.Run("删除 Jenkins 连接", DeleteAsync), _ => CanDelete && !_isBusy);
    }

    public event EventHandler<bool>? DialogClosed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SshServerConfig> SshServers { get; } = new();
    public IReadOnlyList<JenkinsEnvironmentOption> EnvironmentOptions { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand DeleteCommand { get; }
    public bool CanDelete => _editing is not null;

    public string WindowTitle => _isNew ? "添加 Jenkins 连接" : "编辑 Jenkins 连接";
    public bool HasSavedToken => !_isNew && !string.IsNullOrWhiteSpace(_editing?.ApiToken);
    public string TokenHint => HasSavedToken ? "已保存 Token；留空保持不变" : "Jenkins API Token";

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => Set(ref _baseUrl, value);
    }

    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    /// <summary>由 PasswordBox 在 PasswordChanged 时写入；编辑时留空表示沿用旧 Token。</summary>
    public string ApiToken
    {
        get => _apiToken;
        set => Set(ref _apiToken, value);
    }

    public string EnvironmentLabel
    {
        get => _environmentLabel;
        set => Set(ref _environmentLabel, value);
    }

    public string Description
    {
        get => _description;
        set => Set(ref _description, value);
    }

    public string TimeoutText
    {
        get => _timeoutText;
        set => Set(ref _timeoutText, value);
    }

    public bool ReadOnly
    {
        get => _readOnly;
        set => Set(ref _readOnly, value);
    }

    public bool RequireConfirmation
    {
        get => _requireConfirmation;
        set => Set(ref _requireConfirmation, value);
    }

    public bool IgnoreTlsErrors
    {
        get => _ignoreTlsErrors;
        set => Set(ref _ignoreTlsErrors, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    public bool UseSshTunnel
    {
        get => _useSshTunnel;
        set
        {
            if (!Set(ref _useSshTunnel, value))
                return;
            OnPropertyChanged(nameof(CanSelectSshServer));
        }
    }

    public bool CanSelectSshServer => UseSshTunnel;

    public string TunnelSshServerId
    {
        get => _tunnelSshServerId;
        set => Set(ref _tunnelSshServerId, value);
    }

    public int SelectedEnvironmentIndex
    {
        get => _selectedEnvironmentIndex;
        set
        {
            if (!Set(ref _selectedEnvironmentIndex, Math.Clamp(value, 0, EnvironmentOptions.Count - 1)))
                return;

            var option = EnvironmentOptions[_selectedEnvironmentIndex];
            if (_isNew && option.Kind is JenkinsEnvironmentKinds.Production or JenkinsEnvironmentKinds.Partner)
            {
                ReadOnly = true;
                RequireConfirmation = true;
            }
        }
    }

    public JenkinsEnvironmentOption SelectedEnvironment => EnvironmentOptions[_selectedEnvironmentIndex];

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (!Set(ref _statusMessage, value))
                return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStatus)));
        }
    }

    public bool HasStatus => StatusMessage.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value))
                return;
            (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TestConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task TestConnectionAsync()
    {
        if (!TryBuildConfig(out var config, out var error))
        {
            StatusMessage = error;
            return;
        }

        IsBusy = true;
        StatusMessage = "正在测试连接...";
        JenkinsSshTunnel? tunnel = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(config.TunnelSshServerId))
            {
                var sshServer = SshServers.FirstOrDefault(server =>
                    string.Equals(server.Id, config.TunnelSshServerId, StringComparison.Ordinal));
                if (sshServer is null)
                    throw new InvalidOperationException("找不到 SSH 隧道服务器，或服务器已禁用。");

                StatusMessage = $"正在通过 {sshServer.Name} 建立 SSH 隧道...";
                tunnel = await Task.Run(() => JenkinsSshTunnel.Start(
                    sshServer,
                    new FileSshKnownHostsStore(),
                    new SecurityOptionsProvider().SshHostKey.Mode));
            }

            using var client = new JenkinsClient(config, tunnel: tunnel);
            tunnel = null;
            var info = await client.TestConnectionAsync();
            StatusMessage = string.IsNullOrWhiteSpace(info.Version)
                ? $"连接成功：{info.Url}"
                : $"连接成功：Jenkins {info.Version}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接失败：{ex.Message}";
        }
        finally
        {
            tunnel?.Dispose();
            IsBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (_editing is null)
            return;

        if (!_dialogs.Confirm($"确认删除 Jenkins 连接 \"{_editing.DisplayName}\"？", "删除 Jenkins 连接"))
            return;

        IsBusy = true;
        try
        {
            var config = await _configService.LoadConfigAsync();
            if (config.Jenkins is null)
                return;

            config.Jenkins.Connections = (config.Jenkins.Connections ?? Array.Empty<JenkinsConnectionConfig>())
                .Where(item => !string.Equals(item.Id, _editing.Id, StringComparison.Ordinal))
                .ToArray();
            if (string.Equals(config.Jenkins.DefaultConnectionId, _editing.Id, StringComparison.Ordinal))
                config.Jenkins.DefaultConnectionId = config.Jenkins.Connections.FirstOrDefault()?.Id ?? string.Empty;

            await _configService.SaveConfigAsync(config);
            StatusMessage = "已删除";
            DialogClosed?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (!TryBuildConfig(out var input, out var error))
        {
            StatusMessage = error;
            return;
        }

        IsBusy = true;
        try
        {
            var appConfig = await _configService.LoadConfigAsync();
            appConfig.Jenkins ??= new JenkinsConfig();
            var connections = (appConfig.Jenkins.Connections ?? Array.Empty<JenkinsConnectionConfig>()).ToList();

            if (_editing is null)
            {
                input.Id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString("N") : input.Id;
                connections.Add(input);
            }
            else
            {
                var index = connections.FindIndex(item => string.Equals(item.Id, _editing.Id, StringComparison.Ordinal));
                input.Id = _editing.Id;
                if (index >= 0)
                    connections[index] = input;
                else
                    connections.Add(input);
            }

            appConfig.Jenkins.Connections = connections.ToArray();
            if (string.IsNullOrWhiteSpace(appConfig.Jenkins.DefaultConnectionId))
                appConfig.Jenkins.DefaultConnectionId = input.Id;

            await _configService.SaveConfigAsync(appConfig);
            StatusMessage = "已保存";
            DialogClosed?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryBuildConfig(out JenkinsConnectionConfig config, out string error)
    {
        config = new JenkinsConnectionConfig();
        error = string.Empty;

        var name = Name.Trim();
        var url = BaseUrl.Trim();
        var username = Username.Trim();
        var token = string.IsNullOrWhiteSpace(ApiToken) ? _editing?.ApiToken ?? string.Empty : ApiToken.Trim();

        if (name.Length == 0)
        {
            error = "请填写连接名称。";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Jenkins 地址必须是有效的 http/https URL。";
            return false;
        }

        if (username.Length == 0)
        {
            error = "请填写 Jenkins 用户名。";
            return false;
        }

        if (token.Length == 0)
        {
            error = "请填写 Jenkins API Token。";
            return false;
        }

        if (UseSshTunnel && string.IsNullOrWhiteSpace(TunnelSshServerId))
        {
            error = "已开启 SSH 隧道，请选择承载隧道的 SSH 服务器。";
            return false;
        }

        if (!int.TryParse(TimeoutText.Trim(), out var timeout) || timeout < 5 || timeout > 300)
        {
            error = "超时时间必须在 5–300 秒之间。";
            return false;
        }

        config = new JenkinsConnectionConfig
        {
            Id = _editing?.Id ?? string.Empty,
            Name = name,
            EnvironmentKind = SelectedEnvironment.Kind,
            EnvironmentLabel = EnvironmentLabel.Trim(),
            BaseUrl = url.TrimEnd('/'),
            TunnelSshServerId = UseSshTunnel ? TunnelSshServerId.Trim() : string.Empty,
            Username = username,
            ApiToken = token,
            ReadOnly = ReadOnly,
            RequireConfirmation = RequireConfirmation,
            IgnoreTlsErrors = IgnoreTlsErrors,
            TimeoutSeconds = timeout,
            Description = Description.Trim(),
            Enabled = IsEnabled
        };
        return true;
    }

    private async Task LoadSshServersAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            SshServers.Clear();
            foreach (var server in config.Servers ?? Array.Empty<SshServerConfig>())
            {
                if (server is not null && !server.Disabled)
                    SshServers.Add(server);
            }
        }
        catch
        {
            // 连接服务器列表加载失败不阻断表单；保存时仍会校验
        }
    }

    private int FindEnvironmentIndex(string? kind)
    {
        var normalized = JenkinsEnvironmentKinds.Normalize(kind);
        var index = EnvironmentOptions
            .Select((option, i) => (option, i))
            .FirstOrDefault(pair => pair.option.Kind == normalized).i;
        return index;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
