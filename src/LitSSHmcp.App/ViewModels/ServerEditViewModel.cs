using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
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
    private string _username = string.Empty;
    private int _authTypeIndex;
    private string _keyFilePath = string.Empty;
    private string _description = string.Empty;
    private string _tagsText = string.Empty;
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
        BrowseKeyFileCommand = new RelayCommand(_ => BrowseKeyFile());

        if (server != null)
        {
            Name = server.Name;
            Host = server.Host;
            Port = server.Port;
            Username = server.Username;
            AuthTypeIndex = server.AuthType == AuthType.KeyFile ? 1 : 0;
            KeyFilePath = server.KeyFilePath ?? string.Empty;
            Description = server.Description ?? string.Empty;
            TagsText = string.Join(", ", server.Tags);
            SudoTypeIndex = (int)server.SudoType;
            SudoUsername = server.SudoUsername ?? string.Empty;
            Disabled = server.Disabled;
        }
        else if (prefill != null)
        {
            Host = prefill.Host;
            Name = string.IsNullOrWhiteSpace(prefill.Name) ? prefill.Host : prefill.Name;
            Port = prefill.Port > 0 ? prefill.Port : 22;
        }
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
    public ICommand BrowseKeyFileCommand { get; }

    private async void TestConnection()
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

            var result = await _sshService.TestConnectionAsync(server);
            StatusMessage = result ? "连接成功" : "连接失败";
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

    private void BrowseKeyFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "OpenSSH/PEM 密钥|*.pem;*.key;*.*|所有文件|*.*",
            Title = "选择SSH密钥文件（OpenSSH 或 PEM 格式；不支持 PuTTY .ppk）"
        };

        if (dialog.ShowDialog() == true)
        {
            KeyFilePath = dialog.FileName;
        }
    }

    private async void Save()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var server = BuildServerConfig();
            // 留空=未修改，保留原密码/密钥口令/提权密码（编辑任意字段不应清空它们）
            server.Password = Coalesce(_editWindow.GetPassword(), _editingServer?.Password);
            server.KeyFilePassphrase = Coalesce(_editWindow.GetKeyPassword(), _editingServer?.KeyFilePassphrase);
            server.SudoPassword = Coalesce(_editWindow.GetSudoPassword(), _editingServer?.SudoPassword);

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
    }

    private void Cancel()
    {
        DialogClosed?.Invoke(this, false);
    }

    private static string? Coalesce(string? input, string? existing) =>
        string.IsNullOrEmpty(input) ? existing : input;

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
            Username = Username,
            AuthType = IsKeyFileAuth ? AuthType.KeyFile : AuthType.Password,
            KeyFilePath = IsKeyFileAuth ? KeyFilePath : null,
            Description = Description,
            Tags = tags,
            SudoType = (SudoType)SudoTypeIndex,
            SudoUsername = IsCustomSudoUser ? SudoUsername : null,
            Disabled = Disabled
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}