using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public class ServerEditViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly ISshService _sshService;
    private readonly ServerEditWindow _editWindow;
    private SshServerConfig? _editingServer;
    private string _name = string.Empty;
    private string _host = string.Empty;
    private int _port = 22;
    private string _internalHost = string.Empty;
    private string _internalPort = string.Empty;
    private string _username = string.Empty;
    private int _authTypeIndex;
    private string _keyFilePath = string.Empty;
    private string _description = string.Empty;
    private string _tagsText = string.Empty;
    private string _group = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isTestRunning;
    private int _sudoTypeIndex;
    private string _sudoUsername = string.Empty;
    private bool _disabled;

    public event EventHandler<bool>? DialogClosed;

    public ServerEditViewModel(IConfigService configService, ISshService sshService, ServerEditWindow editWindow, SshServerConfig? server = null, SshServerConfig? prefill = null)
    {
        _configService = configService;
        _sshService = sshService;
        _editWindow = editWindow;
        _editingServer = server;

        SaveCommand = new RelayCommand(_ => Save());
        CancelCommand = new RelayCommand(_ => Cancel());
        TestConnectionCommand = new RelayCommand(_ => TestConnection(), _ => !_isTestRunning);
        GenerateKeyCommand = new RelayCommand(_ => GenerateKey());

        if (server != null)
        {
            Name = server.Name;
            Host = server.Host;
            Port = server.Port;
            InternalHost = server.InternalHost ?? string.Empty;
            InternalPort = server.InternalPort is > 0 ? server.InternalPort.Value.ToString() : string.Empty;
            Username = server.Username;
            AuthTypeIndex = server.AuthType == AuthType.KeyFile ? 1 : 0;
            KeyFilePath = server.KeyFilePath ?? string.Empty;
            Description = server.Description ?? string.Empty;
            TagsText = string.Join(", ", server.Tags);
            Group = server.Group ?? string.Empty;
            SudoTypeIndex = (int)server.SudoType;
            SudoUsername = server.SudoUsername ?? string.Empty;
            Disabled = server.Disabled;
            CredentialRotateDays = server.CredentialRotateDays.ToString();
            _credentialBase = server.CredentialUpdatedAt ?? server.CreatedAt;
            ProxyJumpServerId = server.ProxyJumpServerId;
        }
        else if (prefill != null)
        {
            Host = prefill.Host;
            Name = string.IsNullOrWhiteSpace(prefill.Name) ? prefill.Host : prefill.Name;
            Port = prefill.Port > 0 ? prefill.Port : 22;
        }

        _ = LoadJumpOptionsAsync();
    }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string Host
    {
        get => _host;
        set { _host = value; OnPropertyChanged(); }
    }

    public int Port
    {
        get => _port;
        set { _port = value; OnPropertyChanged(); }
    }

    /// <summary>内网地址（可选）：跨机复制时源服务器可用该地址直连目标，走服务器间内网通道；空=回退主机地址。</summary>
    public string InternalHost
    {
        get => _internalHost;
        set { _internalHost = value; OnPropertyChanged(); }
    }

    /// <summary>内网 SSH 端口（可选）：留空沿用上面的端口。</summary>
    public string InternalPort
    {
        get => _internalPort;
        set { _internalPort = value; OnPropertyChanged(); }
    }

    public string Username
    {
        get => _username;
        set { _username = value; OnPropertyChanged(); }
    }

    public int AuthTypeIndex
    {
        get => _authTypeIndex;
        set { _authTypeIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsKeyFileAuth)); }
    }

    public bool IsKeyFileAuth => AuthTypeIndex == 1;

    public string KeyFilePath
    {
        get => _keyFilePath;
        set { _keyFilePath = value; OnPropertyChanged(); }
    }

    private int _keyGenTypeIndex;
    /// <summary>生成密钥类型：0=ed25519，1=rsa(4096)。</summary>
    public int KeyGenTypeIndex
    {
        get => _keyGenTypeIndex;
        set { _keyGenTypeIndex = value; OnPropertyChanged(); }
    }

    private string _publicKeyText = string.Empty;
    /// <summary>刚生成的公钥文本（供复制到目标服务器 authorized_keys）。</summary>
    public string PublicKeyText
    {
        get => _publicKeyText;
        set { _publicKeyText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPublicKey)); }
    }

    public bool HasPublicKey => !string.IsNullOrEmpty(_publicKeyText);

    // ---- 跳板机（ProxyJump） ----

    /// <summary>跳板机候选（第一项为"不使用跳板"）。</summary>
    public ObservableCollection<JumpOption> JumpOptions { get; } = new();

    private string? _proxyJumpServerId;
    public string? ProxyJumpServerId
    {
        get => _proxyJumpServerId;
        set { _proxyJumpServerId = value; OnPropertyChanged(); }
    }

    private JumpOption? _selectedJumpOption;
    public JumpOption? SelectedJumpOption
    {
        get => _selectedJumpOption;
        set { _selectedJumpOption = value; ProxyJumpServerId = value?.Id; OnPropertyChanged(); }
    }

    /// <summary>加载跳板机候选：排除自身、以及"自身也配了跳板"的服务器（保持单级）。</summary>
    private async Task LoadJumpOptionsAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            JumpOptions.Clear();
            JumpOptions.Add(new JumpOption(null, "（不使用跳板）"));
            foreach (var s in config.Servers)
            {
                if (_editingServer is not null && s.Id == _editingServer.Id)
                    continue;
                if (!string.IsNullOrWhiteSpace(s.ProxyJumpServerId))
                    continue;
                // 主机与当前相同的服务器不能作为跳板（保存时还会以最终 Host 再校验一次）。
                if (!string.IsNullOrWhiteSpace(Host) &&
                    string.Equals(s.Host?.Trim(), Host.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                JumpOptions.Add(new JumpOption(s.Id, string.IsNullOrWhiteSpace(s.Name) ? (s.Host ?? string.Empty) : s.Name));
            }
            SelectedJumpOption = JumpOptions.FirstOrDefault(o => o.Id == ProxyJumpServerId) ?? JumpOptions[0];
        }
        catch
        {
            // 读取失败则仅保留"不使用跳板"
        }
    }

    private string _credentialRotateDays = "0";
    /// <summary>凭据轮换提醒（天，0=关）。</summary>
    public string CredentialRotateDays
    {
        get => _credentialRotateDays;
        set { _credentialRotateDays = value; OnPropertyChanged(); NotifyCredential(); }
    }

    private DateTime _credentialBase = DateTime.UtcNow;

    public string CredentialAgeText
    {
        get
        {
            var days = (int)(DateTime.UtcNow - _credentialBase).TotalDays;
            return days < 0 ? "凭据更新时间未记录" : $"凭据上次更新：{days} 天前（{_credentialBase:yyyy-MM-dd}）";
        }
    }

    public string CredentialWarning
    {
        get
        {
            if (int.TryParse(CredentialRotateDays?.Trim(), out var n) && n > 0)
            {
                var days = (int)(DateTime.UtcNow - _credentialBase).TotalDays;
                if (days > n)
                    return $"⚠ 凭据已 {days} 天未轮换（阈值 {n} 天），建议尽快轮换密码/密钥。";
            }
            return string.Empty;
        }
    }

    public Visibility CredentialWarningVisibility =>
        string.IsNullOrEmpty(CredentialWarning) ? Visibility.Collapsed : Visibility.Visible;

    private void NotifyCredential()
    {
        OnPropertyChanged(nameof(CredentialAgeText));
        OnPropertyChanged(nameof(CredentialWarning));
        OnPropertyChanged(nameof(CredentialWarningVisibility));
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

    /// <summary>连接管理器分组名（空=未分组）。</summary>
    public string Group
    {
        get => _group;
        set { _group = value; OnPropertyChanged(); }
    }

    /// <summary>禁用后：不出现在 MCP 的 ssh_list_servers、拓扑不可建链/连接、发现跳过、工具调用一律 server_disabled。</summary>
    public bool Disabled
    {
        get => _disabled;
        set { _disabled = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public int SudoTypeIndex
    {
        get => _sudoTypeIndex;
        set { _sudoTypeIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCustomSudoUser)); }
    }

    public bool IsCustomSudoUser => SudoTypeIndex == 3;

    public string SudoUsername
    {
        get => _sudoUsername;
        set { _sudoUsername = value; OnPropertyChanged(); }
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand GenerateKeyCommand { get; }

    /// <summary>生成 SSH 密钥对（ed25519/rsa）：写入所选私钥路径，填入密钥路径，并展示公钥供复制。</summary>
    private void GenerateKey()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sshDir = Path.Combine(home, ".ssh");
            var safeName = new string((string.IsNullOrWhiteSpace(Name) ? "litssh" : Name)
                .Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "litssh";

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "选择私钥保存位置",
                InitialDirectory = Directory.Exists(sshDir) ? sshDir : home,
                FileName = $"litssh_{safeName}",
                Filter = "私钥文件|*|所有文件|*.*",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(_editWindow) != true)
                return;

            var dir = Path.GetDirectoryName(dialog.FileName) ?? home;
            var file = Path.GetFileName(dialog.FileName);
            var keyType = KeyGenTypeIndex == 1 ? "rsa" : "ed25519";
            var generated = Core.Services.SSH.SshKeyGenerator.Generate(dir, file, keyType, null, "litssh");

            AuthTypeIndex = 1;
            KeyFilePath = generated.PrivateKeyPath;
            PublicKeyText = generated.PublicKeyOpenSsh;
            StatusMessage = (generated.UsedSshKeyGen ? "已用 ssh-keygen 生成密钥。" : "已生成 RSA 密钥（内置回退）。")
                + "请把下方公钥追加到目标服务器的 ~/.ssh/authorized_keys。";
        }
        catch (Exception ex)
        {
            StatusMessage = "生成密钥失败: " + ex.Message;
        }
    }

    private void TestConnection() => AsyncOperation.Run("测试服务器连接", async () =>
    {
        _isTestRunning = true;
        StatusMessage = "测试连接中...";

        try
        {
            var server = BuildServerConfig();
            // 密码框在编辑时不回填；留空表示"未修改"，保留原值（否则编辑任意字段都会清空密码/密钥口令/提权密码）
            server.Password = Coalesce(_editWindow.GetPassword(), _editingServer?.Password);
            server.KeyFilePassphrase = Coalesce(_editWindow.GetKeyPassword(), _editingServer?.KeyFilePassphrase);
            server.SudoPassword = Coalesce(_editWindow.GetSudoPassword(), _editingServer?.SudoPassword);

            var result = await _sshService.ProbeConnectionAsync(server);
            StatusMessage = SshProbeFormatter.Describe(result);
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

    private void Save() => AsyncOperation.Run("保存服务器", async () =>
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var server = BuildServerConfig();

            if (!ValidateJump(config, server, out var jumpError))
            {
                StatusMessage = jumpError!;
                return;
            }

            // 留空=未修改，保留原密码/密钥口令/提权密码（编辑任意字段不应清空它们）
            server.Password = Coalesce(_editWindow.GetPassword(), _editingServer?.Password);
            server.KeyFilePassphrase = Coalesce(_editWindow.GetKeyPassword(), _editingServer?.KeyFilePassphrase);
            server.SudoPassword = Coalesce(_editWindow.GetSudoPassword(), _editingServer?.SudoPassword);

            // 凭据（密码/密钥/口令/提权密码）有变更时刷新「凭据更新时间」，驱动轮换提醒。
            var credentialChanged = _editingServer is null
                || server.Password != _editingServer.Password
                || server.KeyFilePath != _editingServer.KeyFilePath
                || server.KeyFilePassphrase != _editingServer.KeyFilePassphrase
                || server.SudoPassword != _editingServer.SudoPassword;
            if (credentialChanged)
                _credentialBase = DateTime.UtcNow;
            server.CredentialUpdatedAt = _credentialBase;

            if (_editingServer != null)
            {
                server.Id = _editingServer.Id;
                server.CreatedAt = _editingServer.CreatedAt;
                server.LastConnectedAt = _editingServer.LastConnectedAt;

                var index = Array.FindIndex(config.Servers, s => s.Id == _editingServer.Id);
                if (index >= 0)
                    config.Servers[index] = server;
            }
            else
            {
                config.Servers = config.Servers.Append(server).ToArray();
            }

            await _configService.SaveConfigAsync(config);
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

    private static string? Coalesce(string? input, string? existing) =>
        string.IsNullOrEmpty(input) ? existing : input;

    /// <summary>保存前校验跳板机配置（因为 Host 可被编辑，必须在保存时以最终 Host 复核）。</summary>
    private static bool ValidateJump(AppConfig config, SshServerConfig server, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(server.ProxyJumpServerId))
            return true;

        if (string.Equals(server.ProxyJumpServerId, server.Id, StringComparison.OrdinalIgnoreCase))
        {
            error = "跳板机不能是服务器自身。";
            return false;
        }

        var jump = config.Servers.FirstOrDefault(s =>
            string.Equals(s.Id, server.ProxyJumpServerId, StringComparison.OrdinalIgnoreCase));
        if (jump is null)
        {
            error = "找不到所选的跳板机（可能已被删除）；请重新选择。";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(jump.ProxyJumpServerId))
        {
            error = "暂不支持多级跳板：所选跳板机自身也配置了跳板机。";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(server.Host) &&
            string.Equals(jump.Host?.Trim(), server.Host.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            error = "跳板机与当前服务器主机（Host）相同，不能作为跳板；请改选其它服务器或修改主机地址。";
            return false;
        }

        return true;
    }

    private SshServerConfig BuildServerConfig()
    {
        var tags = TagsText.Split(',')
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToArray();

        return new SshServerConfig
        {
            Id = _editingServer?.Id ?? Guid.NewGuid().ToString("N"),
            Name = Name,
            Host = Host,
            Port = Port,
            InternalHost = string.IsNullOrWhiteSpace(InternalHost) ? null : InternalHost.Trim(),
            InternalPort = int.TryParse(InternalPort?.Trim(), out var internalPort) && internalPort > 0 ? internalPort : null,
            Username = Username,
            AuthType = IsKeyFileAuth ? AuthType.KeyFile : AuthType.Password,
            KeyFilePath = IsKeyFileAuth ? KeyFilePath : null,
            Description = Description,
            Tags = tags,
            Group = Group,
            SudoType = (SudoType)SudoTypeIndex,
            SudoUsername = IsCustomSudoUser ? SudoUsername : null,
            Disabled = Disabled,
            CredentialRotateDays = int.TryParse(CredentialRotateDays?.Trim(), out var rotateDays) && rotateDays > 0 ? rotateDays : 0,
            CredentialUpdatedAt = _credentialBase,
            ProxyJumpServerId = string.IsNullOrWhiteSpace(ProxyJumpServerId) ? null : ProxyJumpServerId
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>跳板机下拉项（Id 为空表示"不使用跳板"）。</summary>
public sealed record JumpOption(string? Id, string Name);