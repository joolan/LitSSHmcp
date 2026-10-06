using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

/// <summary>端口转发管理（本地/远程/动态 SOCKS），定义持久化到 config.json。</summary>
public sealed class PortForwardViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly PortForwardService _service;

    public PortForwardViewModel(IConfigService configService, PortForwardService service, IEnumerable<SshServerConfig> servers)
    {
        _configService = configService;
        _service = service;
        ServerList = servers.ToList();

        AddCommand = new RelayCommand(_ => Add());
        SaveCommand = new RelayCommand(_ => _ = SaveAsync());
        Load();
    }

    public IReadOnlyList<SshServerConfig> ServerList { get; }
    public static PortForwardType[] Types { get; } = Enum.GetValues<PortForwardType>();

    public ObservableCollection<PortForwardRow> Rows { get; } = new();

    public ICommand AddCommand { get; }
    public ICommand SaveCommand { get; }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private async void Load()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            Rows.Clear();
            foreach (var pf in config.PortForwards)
            {
                var row = PortForwardRow.From(pf, ServerList);
                Rows.Add(row);
                if (pf.AutoStart)
                    await StartAsync(row);
            }
            StatusMessage = $"已加载 {Rows.Count} 条转发定义";
        }
        catch (Exception ex)
        {
            StatusMessage = "加载失败: " + ex.Message;
        }
    }

    private void Add()
    {
        var row = new PortForwardRow(ServerList)
        {
            Name = "新转发",
            ServerId = ServerList.FirstOrDefault()?.Id ?? string.Empty,
            BindPortText = "8080",
            TargetPortText = "80"
        };
        Rows.Add(row);
    }

    public void Remove(PortForwardRow row)
    {
        _ = StopAsync(row);
        Rows.Remove(row);
    }

    public async Task StartAsync(PortForwardRow row)
    {
        var server = ServerList.FirstOrDefault(s => s.Id == row.ServerId);
        if (server is null)
        {
            row.Status = "未选择服务器";
            return;
        }

        row.Status = "启动中…";
        var config = row.ToConfig();
        var (ok, message) = await _service.StartAsync(server, config);
        row.IsActive = ok;
        row.Status = message;
    }

    public Task StopAsync(PortForwardRow row)
    {
        row.IsActive = false;
        row.Status = "已停止";
        return _service.StopAsync(row.Id);
    }

    public async Task SaveAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            config.PortForwards = Rows.Select(r => r.ToConfig()).ToArray();
            await _configService.SaveConfigAsync(config);
            StatusMessage = "已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = "保存失败: " + ex.Message;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>端口转发列表中的一行（可编辑）。</summary>
public sealed class PortForwardRow : INotifyPropertyChanged
{
    public PortForwardRow(IReadOnlyList<SshServerConfig> servers) => Servers = servers;

    public IReadOnlyList<SshServerConfig> Servers { get; }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _name = string.Empty;
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }

    private string _serverId = string.Empty;
    public string ServerId { get => _serverId; set { _serverId = value; OnPropertyChanged(); } }

    private PortForwardType _type = PortForwardType.Local;
    public PortForwardType Type { get => _type; set { _type = value; OnPropertyChanged(); } }

    private string _bindHost = "127.0.0.1";
    public string BindHost { get => _bindHost; set { _bindHost = value; OnPropertyChanged(); } }

    private string _bindPortText = "0";
    public string BindPortText { get => _bindPortText; set { _bindPortText = value; OnPropertyChanged(); } }

    private string _targetHost = "127.0.0.1";
    public string TargetHost { get => _targetHost; set { _targetHost = value; OnPropertyChanged(); } }

    private string _targetPortText = "0";
    public string TargetPortText { get => _targetPortText; set { _targetPortText = value; OnPropertyChanged(); } }

    private bool _autoStart;
    public bool AutoStart { get => _autoStart; set { _autoStart = value; OnPropertyChanged(); } }

    private bool _isActive;
    public bool IsActive { get => _isActive; set { _isActive = value; OnPropertyChanged(); } }

    private string _status = "未启动";
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    public static PortForwardRow From(PortForwardConfig c, IReadOnlyList<SshServerConfig> servers) => new(servers)
    {
        Id = c.Id,
        Name = c.Name,
        ServerId = c.ServerId,
        Type = c.Type,
        BindHost = c.BindHost,
        BindPortText = c.BindPort.ToString(),
        TargetHost = c.TargetHost,
        TargetPortText = c.TargetPort.ToString(),
        AutoStart = c.AutoStart
    };

    public PortForwardConfig ToConfig() => new()
    {
        Id = Id,
        Name = Name,
        ServerId = ServerId,
        Type = Type,
        BindHost = BindHost,
        BindPort = int.TryParse(BindPortText, out var bp) ? bp : 0,
        TargetHost = TargetHost,
        TargetPort = int.TryParse(TargetPortText, out var tp) ? tp : 0,
        AutoStart = AutoStart
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
