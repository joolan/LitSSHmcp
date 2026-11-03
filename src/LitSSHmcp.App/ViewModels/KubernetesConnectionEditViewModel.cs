using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Kubernetes;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.ViewModels;

public sealed record KubernetesEnvironmentOption(string Kind, string Label);
public sealed record KubernetesProviderOption(string Kind, string Label);
public sealed record KubernetesAccessOption(string Kind, string Label);

public sealed class KubernetesConnectionEditViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly ISshService _sshService;
    private readonly IUserDialogService _dialogs;
    private readonly KubernetesConnectionConfig? _editing;
    private readonly bool _isNew;

    private string _name = string.Empty;
    private string _environmentLabel = string.Empty;
    private string _description = string.Empty;
    private string _kubeconfigPath = string.Empty;
    private string _kubeconfigContent = string.Empty;
    private string _contextName = string.Empty;
    private string _defaultNamespace = string.Empty;
    private string _kubectlPath = "kubectl";
    private string _remoteSshServerId = string.Empty;
    private string _remoteKubeconfigPath = string.Empty;
    private string _remoteKubectlPath = "kubectl";
    private string _timeoutText = "30";
    private int _selectedEnvironmentIndex;
    private int _selectedProviderIndex;
    private int _selectedAccessIndex;
    private bool _readOnly = true;
    private bool _allowWrites;
    private bool _requireConfirmation = true;
    private bool _isEnabled = true;
    private bool _clearSavedContent;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    public KubernetesConnectionEditViewModel(
        IConfigService configService,
        ISshService sshService,
        KubernetesConnectionConfig? editing = null,
        IUserDialogService? dialogs = null)
    {
        _configService = configService;
        _sshService = sshService;
        _dialogs = dialogs ?? new UserDialogService();
        _editing = editing;
        _isNew = editing is null;

        EnvironmentOptions =
        [
            new(KubernetesEnvironmentKinds.SelfTest, "自检/测试"),
            new(KubernetesEnvironmentKinds.Test, "测试"),
            new(KubernetesEnvironmentKinds.Staging, "预发布"),
            new(KubernetesEnvironmentKinds.Production, "生产"),
            new(KubernetesEnvironmentKinds.Cloud, "云上集群"),
            new(KubernetesEnvironmentKinds.Custom, "自定义")
        ];
        ProviderOptions =
        [
            new(KubernetesProviders.Generic, "通用 Kubernetes"),
            new(KubernetesProviders.K3s, "K3s"),
            new(KubernetesProviders.Aliyun, "阿里云 ACK"),
            new(KubernetesProviders.Aws, "AWS EKS"),
            new(KubernetesProviders.Azure, "Azure AKS"),
            new(KubernetesProviders.Custom, "自定义")
        ];
        AccessOptions =
        [
            new(KubernetesAccessModes.LocalKubectl, "本机 kubectl"),
            new(KubernetesAccessModes.RemoteSsh, "通过 SSH 服务器执行 kubectl")
        ];

        if (editing is not null)
        {
            Name = editing.Name;
            EnvironmentLabel = editing.EnvironmentLabel;
            Description = editing.Description;
            KubeconfigPath = editing.KubeconfigPath;
            ContextName = editing.ContextName;
            DefaultNamespace = editing.DefaultNamespace;
            KubectlPath = editing.KubectlPath;
            RemoteSshServerId = editing.RemoteSshServerId;
            RemoteKubeconfigPath = editing.RemoteKubeconfigPath;
            RemoteKubectlPath = editing.RemoteKubectlPath;
            TimeoutText = editing.TimeoutSeconds > 0 ? editing.TimeoutSeconds.ToString() : "30";
            ReadOnly = editing.ReadOnly;
            AllowWrites = !editing.ReadOnly;
            RequireConfirmation = editing.RequireConfirmation;
            IsEnabled = editing.Enabled;
            SelectedEnvironmentIndex = FindIndex(EnvironmentOptions, item => item.Kind,
                KubernetesEnvironmentKinds.Normalize(editing.EnvironmentKind));
            SelectedProviderIndex = FindIndex(ProviderOptions, item => item.Kind,
                KubernetesProviders.Normalize(editing.Provider));
            SelectedAccessIndex = FindIndex(AccessOptions, item => item.Kind,
                KubernetesAccessModes.Normalize(editing.AccessMode));
            HasSavedKubeconfigContent = !string.IsNullOrWhiteSpace(editing.KubeconfigContent);
        }

        _ = LoadSshServersAsync();

        SaveCommand = new RelayCommand(_ => AsyncOperation.Run("保存 K8s 连接", SaveAsync), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => DialogClosed?.Invoke(this, false));
        TestConnectionCommand = new RelayCommand(
            _ => AsyncOperation.Run("测试 K8s 连接", TestConnectionAsync),
            _ => !IsBusy);
        DeleteCommand = new RelayCommand(
            _ => AsyncOperation.Run("删除 K8s 连接", DeleteAsync),
            _ => CanDelete && !IsBusy);
    }

    public event EventHandler<bool>? DialogClosed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SshServerConfig> SshServers { get; } = new();
    public IReadOnlyList<KubernetesEnvironmentOption> EnvironmentOptions { get; }
    public IReadOnlyList<KubernetesProviderOption> ProviderOptions { get; }
    public IReadOnlyList<KubernetesAccessOption> AccessOptions { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand DeleteCommand { get; }
    public bool CanDelete => _editing is not null;
    public bool HasSavedKubeconfigContent { get; }
    public string WindowTitle => _isNew ? "添加 K8s 集群连接" : "编辑 K8s 集群连接";
    public string KubeconfigHint => HasSavedKubeconfigContent && !ClearSavedContent
        ? "已保存 kubeconfig 内容；留空保持不变"
        : "粘贴 kubeconfig 内容，保存时使用 DPAPI 加密";

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
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

    public string KubeconfigPath
    {
        get => _kubeconfigPath;
        set => Set(ref _kubeconfigPath, value);
    }

    public string KubeconfigContent
    {
        get => _kubeconfigContent;
        set => Set(ref _kubeconfigContent, value);
    }

    public string ContextName
    {
        get => _contextName;
        set => Set(ref _contextName, value);
    }

    public string DefaultNamespace
    {
        get => _defaultNamespace;
        set => Set(ref _defaultNamespace, value);
    }

    public string KubectlPath
    {
        get => _kubectlPath;
        set => Set(ref _kubectlPath, value);
    }

    public string RemoteSshServerId
    {
        get => _remoteSshServerId;
        set => Set(ref _remoteSshServerId, value);
    }

    public string RemoteKubeconfigPath
    {
        get => _remoteKubeconfigPath;
        set => Set(ref _remoteKubeconfigPath, value);
    }

    public string RemoteKubectlPath
    {
        get => _remoteKubectlPath;
        set => Set(ref _remoteKubectlPath, value);
    }

    public string TimeoutText
    {
        get => _timeoutText;
        set => Set(ref _timeoutText, value);
    }

    public int SelectedEnvironmentIndex
    {
        get => _selectedEnvironmentIndex;
        set
        {
            if (!Set(ref _selectedEnvironmentIndex, Math.Clamp(value, 0, EnvironmentOptions.Count - 1)))
                return;
            OnPropertyChanged(nameof(SelectedEnvironment));
            if (_isNew && KubernetesEnvironmentKinds.IsHighRisk(SelectedEnvironment.Kind))
            {
                ReadOnly = true;
                AllowWrites = false;
                RequireConfirmation = true;
            }
        }
    }

    public int SelectedProviderIndex
    {
        get => _selectedProviderIndex;
        set
        {
            if (Set(ref _selectedProviderIndex, Math.Clamp(value, 0, ProviderOptions.Count - 1)))
                OnPropertyChanged(nameof(SelectedProvider));
        }
    }

    public int SelectedAccessIndex
    {
        get => _selectedAccessIndex;
        set
        {
            if (!Set(ref _selectedAccessIndex, Math.Clamp(value, 0, AccessOptions.Count - 1)))
                return;
            OnPropertyChanged(nameof(SelectedAccess));
            OnPropertyChanged(nameof(IsRemote));
            OnPropertyChanged(nameof(IsLocal));
        }
    }

    public KubernetesEnvironmentOption SelectedEnvironment => EnvironmentOptions[_selectedEnvironmentIndex];
    public KubernetesProviderOption SelectedProvider => ProviderOptions[_selectedProviderIndex];
    public KubernetesAccessOption SelectedAccess => AccessOptions[_selectedAccessIndex];
    public bool IsRemote => SelectedAccess.Kind == KubernetesAccessModes.RemoteSsh;
    public bool IsLocal => !IsRemote;
    public bool ReadOnly
    {
        get => _readOnly;
        set => Set(ref _readOnly, value);
    }

    public bool AllowWrites
    {
        get => _allowWrites;
        set => Set(ref _allowWrites, value);
    }
    public bool RequireConfirmation
    {
        get => _requireConfirmation;
        set => Set(ref _requireConfirmation, value);
    }
    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    public bool ClearSavedContent
    {
        get => _clearSavedContent;
        set
        {
            if (!Set(ref _clearSavedContent, value))
                return;
            OnPropertyChanged(nameof(KubeconfigHint));
        }
    }

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
        StatusMessage = IsRemote ? "正在通过 SSH 服务器执行 kubectl…" : "正在执行 kubectl…";
        try
        {
            var server = FindServer(config);
            var service = new KubernetesService(_sshService);
            var result = await service.TestConnectionAsync(config, server);
            StatusMessage = $"连接成功：{result.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接失败：{ex.Message}";
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
            var appConfig = await _configService.LoadConfigAsync().ConfigureAwait(true);
            appConfig.Kubernetes ??= new KubernetesConfig();
            var connections = (appConfig.Kubernetes.Connections ?? Array.Empty<KubernetesConnectionConfig>()).ToList();
            if (_editing is null)
            {
                input.Id = Guid.NewGuid().ToString("N");
                connections.Add(input);
            }
            else
            {
                var index = connections.FindIndex(item => item.Id == _editing.Id);
                input.Id = _editing.Id;
                if (index >= 0)
                    connections[index] = input;
                else
                    connections.Add(input);
            }

            appConfig.Kubernetes.Connections = connections.ToArray();
            if (string.IsNullOrWhiteSpace(appConfig.Kubernetes.DefaultConnectionId))
                appConfig.Kubernetes.DefaultConnectionId = input.Id;

            await _configService.SaveConfigAsync(appConfig).ConfigureAwait(true);
            StatusMessage = "已保存。";
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

    private async Task DeleteAsync()
    {
        if (_editing is null)
            return;
        if (!_dialogs.Confirm($"确认删除 K8s 连接 \"{_editing.DisplayName}\"？", "删除 K8s 连接"))
            return;

        IsBusy = true;
        try
        {
            var appConfig = await _configService.LoadConfigAsync().ConfigureAwait(true);
            appConfig.Kubernetes ??= new KubernetesConfig();
            appConfig.Kubernetes.Connections = (appConfig.Kubernetes.Connections ?? Array.Empty<KubernetesConnectionConfig>())
                .Where(item => item.Id != _editing.Id)
                .ToArray();
            if (appConfig.Kubernetes.DefaultConnectionId == _editing.Id)
                appConfig.Kubernetes.DefaultConnectionId = appConfig.Kubernetes.Connections.FirstOrDefault()?.Id ?? string.Empty;
            await _configService.SaveConfigAsync(appConfig).ConfigureAwait(true);
            StatusMessage = "已删除。";
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

    private bool TryBuildConfig(out KubernetesConnectionConfig config, out string error)
    {
        config = new KubernetesConnectionConfig();
        error = string.Empty;
        var name = Name.Trim();
        if (name.Length == 0)
        {
            error = "请填写连接名称。";
            return false;
        }

        if (!int.TryParse(TimeoutText.Trim(), out var timeout) || timeout < 5 || timeout > 300)
        {
            error = "超时时间必须在 5～300 秒之间。";
            return false;
        }

        if (IsRemote && string.IsNullOrWhiteSpace(RemoteSshServerId))
        {
            error = "远端 kubectl 模式必须选择一台 SSH 服务器。";
            return false;
        }

        var content = string.IsNullOrWhiteSpace(KubeconfigContent)
            ? _clearSavedContent ? string.Empty : _editing?.KubeconfigContent ?? string.Empty
            : KubeconfigContent.Trim();

        config = new KubernetesConnectionConfig
        {
            Id = _editing?.Id ?? string.Empty,
            Name = name,
            EnvironmentKind = SelectedEnvironment.Kind,
            EnvironmentLabel = EnvironmentLabel.Trim(),
            Provider = SelectedProvider.Kind,
            AccessMode = SelectedAccess.Kind,
            KubeconfigPath = KubeconfigPath.Trim(),
            KubeconfigContent = content,
            ContextName = ContextName.Trim(),
            DefaultNamespace = DefaultNamespace.Trim(),
            KubectlPath = string.IsNullOrWhiteSpace(KubectlPath) ? "kubectl" : KubectlPath.Trim(),
            RemoteSshServerId = RemoteSshServerId.Trim(),
            RemoteKubeconfigPath = RemoteKubeconfigPath.Trim(),
            RemoteKubectlPath = string.IsNullOrWhiteSpace(RemoteKubectlPath) ? "kubectl" : RemoteKubectlPath.Trim(),
            ReadOnly = !AllowWrites,
            RequireConfirmation = RequireConfirmation,
            Enabled = IsEnabled,
            TimeoutSeconds = timeout,
            Description = Description.Trim()
        };
        return true;
    }

    private SshServerConfig? FindServer(KubernetesConnectionConfig config)
    {
        if (!config.IsRemote)
            return null;
        return SshServers.FirstOrDefault(item => item.Id == config.RemoteSshServerId);
    }

    private async Task LoadSshServersAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync().ConfigureAwait(true);
            SshServers.Clear();
            foreach (var server in config.Servers ?? Array.Empty<SshServerConfig>())
            {
                if (!server.Disabled)
                    SshServers.Add(server);
            }
        }
        catch
        {
        }
    }

    private static int FindIndex<T>(IReadOnlyList<T> items, Func<T, string> key, string value)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(key(items[i]), value, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

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
