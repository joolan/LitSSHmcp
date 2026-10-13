using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

/// <summary>远程桌面连接定义的新增 / 编辑窗口视图模型。</summary>
public class RemoteDesktopEditViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly RemoteDesktopEditWindow _editWindow;
    private readonly RemoteDesktopConfig? _editing;
    private readonly SshServerConfig[] _servers;

    private string _name = string.Empty;
    private int _protocolIndex;
    private string _host = string.Empty;
    private string _portText = "3389";
    private string _username = string.Empty;
    private string _domain = string.Empty;
    private int _tunnelServerIndex;
    private string _group = string.Empty;
    private string _tagsText = string.Empty;
    private string _description = string.Empty;
    private bool _fullscreen;
    private bool _useLoginCredentials = true;
    private string _viewerPath = string.Empty;
    private int _resolutionIndex;
    private string _customWidthText = string.Empty;
    private string _customHeightText = string.Empty;
    private string _statusMessage = string.Empty;

    public event EventHandler<bool>? DialogClosed;

    public RemoteDesktopEditViewModel(IConfigService configService, RemoteDesktopEditWindow editWindow, SshServerConfig[] servers, RemoteDesktopConfig? desktop = null)
    {
        _configService = configService;
        _editWindow = editWindow;
        _editing = desktop;
        _servers = servers ?? Array.Empty<SshServerConfig>();
        TunnelServerOptions = new[] { "(直连，不经 SSH 隧道)" }
            .Concat(_servers.Select(s => s.Disabled ? $"{s.Name} - {s.Host}（已禁用）" : $"{s.Name} - {s.Host}"))
            .ToArray();

        SaveCommand = new RelayCommand(_ => Save());
        CancelCommand = new RelayCommand(_ => DialogClosed?.Invoke(this, false));
        BrowseViewerCommand = new RelayCommand(_ => BrowseViewer());

        if (desktop != null)
        {
            Name = desktop.Name;
            ProtocolIndex = desktop.Protocol == RemoteDesktopProtocol.Vnc ? 1 : 0;
            Host = desktop.Host;
            PortText = desktop.Port.ToString();
            Username = desktop.Username ?? string.Empty;
            Domain = desktop.Domain ?? string.Empty;
            TunnelServerIndex = ResolveTunnelIndex(desktop.TunnelServerId);
            Group = desktop.Group;
            TagsText = string.Join(", ", desktop.Tags);
            Description = desktop.Description ?? string.Empty;
            Fullscreen = desktop.Fullscreen;
            UseLoginCredentials = desktop.UseLoginCredentials;
            ViewerPath = desktop.ViewerPath ?? string.Empty;
            ResolutionIndex = MatchResolution(desktop.DesktopWidth, desktop.DesktopHeight);
            if (ResolutionIndex == CustomResolutionIndex)
            {
                CustomWidthText = desktop.DesktopWidth?.ToString() ?? string.Empty;
                CustomHeightText = desktop.DesktopHeight?.ToString() ?? string.Empty;
            }
        }
    }

    public string[] TunnelServerOptions { get; }

    /// <summary>自定义分辨率在预设列表中的索引。</summary>
    public const int CustomResolutionIndex = 6;

    /// <summary>RDP 桌面分辨率预设（索引 0 = 自适应，6 = 自定义）。</summary>
    public IReadOnlyList<string> ResolutionOptions { get; } = new[]
    {
        "自适应", "1280 × 720", "1600 × 900", "1920 × 1080", "2560 × 1440", "3840 × 2160", "自定义…"
    };

    // 索引 0..5 为固定预设；索引 6 用自定义宽高
    private static readonly (int? W, int? H)[] ResolutionPresets =
    {
        (null, null), (1280, 720), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160), (null, null)
    };

    public bool IsVnc => ProtocolIndex == 1;

    public bool IsRdp => ProtocolIndex == 0;

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }

    public int ProtocolIndex
    {
        get => _protocolIndex;
        set
        {
            if (_protocolIndex == value) return;
            var previousDefault = DefaultPort(_protocolIndex);
            _protocolIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVnc));
            OnPropertyChanged(nameof(IsRdp));

            if (string.IsNullOrWhiteSpace(PortText) || PortText.Trim() == previousDefault)
                PortText = DefaultPort(value);
        }
    }

    private static string DefaultPort(int protocolIndex) => protocolIndex == 1 ? "5900" : "3389";

    public string Host { get => _host; set { _host = value; OnPropertyChanged(); } }
    public string PortText { get => _portText; set { _portText = value; OnPropertyChanged(); } }
    public string Username { get => _username; set { _username = value; OnPropertyChanged(); } }
    public string Domain { get => _domain; set { _domain = value; OnPropertyChanged(); } }

    public int TunnelServerIndex { get => _tunnelServerIndex; set { _tunnelServerIndex = value; OnPropertyChanged(); } }
    public string Group { get => _group; set { _group = value; OnPropertyChanged(); } }
    public string TagsText { get => _tagsText; set { _tagsText = value; OnPropertyChanged(); } }
    public string Description { get => _description; set { _description = value; OnPropertyChanged(); } }
    public bool Fullscreen { get => _fullscreen; set { _fullscreen = value; OnPropertyChanged(); } }
    public bool UseLoginCredentials { get => _useLoginCredentials; set { _useLoginCredentials = value; OnPropertyChanged(); } }
    public string ViewerPath { get => _viewerPath; set { _viewerPath = value; OnPropertyChanged(); } }

    /// <summary>RDP 桌面分辨率预设索引（0 = 自适应，6 = 自定义）。</summary>
    public int ResolutionIndex
    {
        get => _resolutionIndex;
        set { _resolutionIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCustomResolution)); }
    }

    public bool IsCustomResolution => ResolutionIndex == CustomResolutionIndex;

    public string CustomWidthText { get => _customWidthText; set { _customWidthText = value; OnPropertyChanged(); } }
    public string CustomHeightText { get => _customHeightText; set { _customHeightText = value; OnPropertyChanged(); } }

    public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseViewerCommand { get; }

    private int ResolveTunnelIndex(string? serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return 0;
        var index = Array.FindIndex(_servers, s => s.Id == serverId);
        return index < 0 ? 0 : index + 1;
    }

    private void BrowseViewer()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 VNC 查看器可执行文件",
            Filter = "可执行文件|*.exe|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
            ViewerPath = dialog.FileName;
    }

    private async void Save()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Host))
            {
                StatusMessage = "目标主机地址不能为空";
                return;
            }
            if (!int.TryParse(PortText, out var port) || port is < 1 or > 65535)
            {
                StatusMessage = "端口须为 1-65535";
                return;
            }
            if (IsCustomResolution)
            {
                if (!int.TryParse(CustomWidthText, out var cw) || cw is < 640 or > 7680
                    || !int.TryParse(CustomHeightText, out var ch) || ch is < 480 or > 4320)
                {
                    StatusMessage = "自定义分辨率无效：宽 640-7680、高 480-4320";
                    return;
                }
            }

            var config = await _configService.LoadConfigAsync();
            var rd = Build();

            if (_editing != null)
            {
                rd.Id = _editing.Id;
                rd.CreatedAt = _editing.CreatedAt;
                rd.LastConnectedAt = _editing.LastConnectedAt;
                rd.Password = _editWindow.GetPassword() is { Length: > 0 } pwd ? pwd : _editing.Password;
                var index = Array.FindIndex(config.RemoteDesktops, r => r.Id == _editing.Id);
                if (index >= 0)
                    config.RemoteDesktops[index] = rd;
                else
                    config.RemoteDesktops = config.RemoteDesktops.Append(rd).ToArray();
            }
            else
            {
                rd.Password = _editWindow.GetPassword();
                config.RemoteDesktops = config.RemoteDesktops.Append(rd).ToArray();
            }

            await _configService.SaveConfigAsync(config);
            DialogClosed?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            StatusMessage = "保存失败: " + ex.Message;
        }
    }

    private RemoteDesktopConfig Build()
    {
        var tags = TagsText.Split(',')
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToArray();

        string? tunnelServerId = null;
        if (TunnelServerIndex > 0 && TunnelServerIndex - 1 < _servers.Length)
            tunnelServerId = _servers[TunnelServerIndex - 1].Id;

        (int? W, int? H) res = ResolutionIndex is >= 1 and <= 5
            ? ResolutionPresets[ResolutionIndex]
            : (null, null);
        if (ResolutionIndex == CustomResolutionIndex
            && int.TryParse(CustomWidthText, out var cw) && int.TryParse(CustomHeightText, out var ch))
            res = (cw, ch);
        var (resW, resH) = res;

        return new RemoteDesktopConfig
        {
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(Name) ? Host : Name.Trim(),
            Protocol = ProtocolIndex == 1 ? RemoteDesktopProtocol.Vnc : RemoteDesktopProtocol.Rdp,
            Host = Host.Trim(),
            Port = int.TryParse(PortText, out var p) ? p : DefaultPortInt(),
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            Domain = string.IsNullOrWhiteSpace(Domain) ? null : Domain.Trim(),
            TunnelServerId = tunnelServerId,
            Group = Group?.Trim() ?? string.Empty,
            Tags = tags,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Fullscreen = Fullscreen,
            UseLoginCredentials = UseLoginCredentials,
            ViewerPath = string.IsNullOrWhiteSpace(ViewerPath) ? null : ViewerPath.Trim(),
            DesktopWidth = resW,
            DesktopHeight = resH
        };
    }

    /// <summary>把配置里的宽高匹配到预设索引；无匹配的自定义值返回自定义索引（6）；无值返回 0（自适应）。</summary>
    private static int MatchResolution(int? w, int? h)
    {
        if (w is not > 0 || h is not > 0)
            return 0;
        for (var i = 1; i <= 5; i++)
            if (ResolutionPresets[i].W == w && ResolutionPresets[i].H == h)
                return i;
        return CustomResolutionIndex;
    }

    private int DefaultPortInt() => ProtocolIndex == 1 ? 5900 : 3389;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
