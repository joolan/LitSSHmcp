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
public sealed class PortForwardViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly HashSet<string> DefinitionProps = new()
    {
        nameof(PortForwardRow.Name), nameof(PortForwardRow.ServerId), nameof(PortForwardRow.Type),
        nameof(PortForwardRow.BindHost), nameof(PortForwardRow.BindPortText),
        nameof(PortForwardRow.TargetHost), nameof(PortForwardRow.TargetPortText),
        nameof(PortForwardRow.AutoStart)
    };

    private readonly IConfigService _configService;
    private readonly PortForwardService _service;

    public PortForwardViewModel(IConfigService configService, PortForwardService service, IEnumerable<SshServerConfig> servers)
    {
        _configService = configService;
        _service = service;
        ServerList = servers.ToList();

        AddCommand = new RelayCommand(_ => Add());
        SaveCommand = new RelayCommand(_ => _ = SaveAsync());
        _service.StateChanged += OnServiceStateChanged;
        Load();
    }

    public IReadOnlyList<SshServerConfig> ServerList { get; }

    /// <summary>转发类型下拉（带中文说明）。</summary>
    public IReadOnlyList<PortForwardTypeOption> TypeOptions { get; } = new[]
    {
        new PortForwardTypeOption(PortForwardType.Local, "本地 Local（-L）"),
        new PortForwardTypeOption(PortForwardType.Remote, "远程 Remote（-R）"),
        new PortForwardTypeOption(PortForwardType.Dynamic, "动态 SOCKS（-D）")
    };

    public ObservableCollection<PortForwardRow> Rows { get; } = new();

    public ICommand AddCommand { get; }
    public ICommand SaveCommand { get; }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private bool _isDirty;
    /// <summary>定义（非运行状态）是否有未保存修改。</summary>
    public bool IsDirty
    {
        get => _isDirty;
        set { _isDirty = value; OnPropertyChanged(); }
    }

    /// <summary>后台隧道状态变化（断开/自动重连）→ 更新对应行显示。线程池线程触发，调度到 UI 线程。</summary>
    private void OnServiceStateChanged(string id, bool active, string message)
        => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Id == id);
            if (row is null)
                return;
            row.IsRunning = active;
            row.IsError = !active;
            row.Status = message;
        }));

    private async void Load()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            Rows.Clear();
            foreach (var pf in config.PortForwards)
            {
                var row = PortForwardRow.From(pf, ServerList);
                HookDirty(row);
                Rows.Add(row);

                if (_service.IsActive(row.Id))
                {
                    // 关窗后隧道仍在后台运行：重开窗口时同步真实状态，避免误显示"未启动"
                    row.IsRunning = true;
                    row.Status = "已在后台运行";
                }
                else if (pf.AutoStart)
                {
                    await StartAsync(row);
                }
            }
            IsDirty = false;
            StatusMessage = $"已加载 {Rows.Count} 条转发定义";
        }
        catch (Exception ex)
        {
            StatusMessage = "加载失败: " + ex.Message;
        }
    }

    private void HookDirty(PortForwardRow row)
        => row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null && DefinitionProps.Contains(e.PropertyName))
                IsDirty = true;
        };

    private void Add()
    {
        var row = new PortForwardRow(ServerList)
        {
            Name = "新转发",
            ServerId = ServerList.FirstOrDefault()?.Id ?? string.Empty,
            BindPortText = "8080",
            TargetPortText = "80"
        };
        HookDirty(row);
        Rows.Add(row);
        IsDirty = true;
    }

    public void Remove(PortForwardRow row)
    {
        _ = StopAsync(row);
        Rows.Remove(row);
        IsDirty = true;
    }

    public async Task StartAsync(PortForwardRow row)
    {
        if (row.IsBusy)
            return;
        row.IsBusy = true;
        try
        {
            var server = ServerList.FirstOrDefault(s => s.Id == row.ServerId);
            if (server is null)
            {
                row.IsRunning = false;
                row.IsError = true;
                row.Status = string.IsNullOrEmpty(row.ServerId) ? "未选择服务器" : "服务器已删除或不存在";
                return;
            }

            if (!row.Validate(out var error))
            {
                row.IsRunning = false;
                row.IsError = true;
                row.Status = error;
                return;
            }

            row.IsError = false;
            row.Status = "启动中…";
            var config = row.ToConfig();
            var (ok, message) = await _service.StartAsync(server, config);
            row.IsRunning = ok;
            row.IsError = !ok;
            row.Status = message;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    public async Task StopAsync(PortForwardRow row)
    {
        if (row.IsBusy)
            return;
        row.IsBusy = true;
        try
        {
            row.IsRunning = false;
            row.IsError = false;
            row.Status = "已停止";
            await _service.StopAsync(row.Id);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            config.PortForwards = Rows.Select(r => r.ToConfig()).ToArray();
            await _configService.SaveConfigAsync(config);
            IsDirty = false;
            var invalid = Rows.Count(r => !r.Validate(out _));
            StatusMessage = invalid > 0
                ? $"已保存（{invalid} 条定义不完整，修正后才能启动）"
                : "已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = "保存失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 关窗时的兜底保存：同步等待写盘完成（文件小、毫秒级），保证窗口关闭/进程退出前定义不丢。
    /// 在线程池上执行整条保存链路，不依赖 Dispatcher，退出竞态窗口内也能完成。
    /// </summary>
    public void SaveOnClose()
    {
        if (!IsDirty)
            return;
        try
        {
            Task.Run(SaveAsync).Wait(3000);
        }
        catch
        {
            // 保存失败不影响关闭
        }
    }

    public void Dispose()
    {
        _service.StateChanged -= OnServiceStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>转发类型下拉项。</summary>
public sealed record PortForwardTypeOption(PortForwardType Value, string Label);

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

    private bool _isRunning;
    /// <summary>隧道当前是否在运行（含后台自动重连成功态）。</summary>
    public bool IsRunning { get => _isRunning; set { _isRunning = value; OnPropertyChanged(); } }

    private bool _isError;
    /// <summary>最近一次操作/状态为失败或断开。</summary>
    public bool IsError { get => _isError; set { _isError = value; OnPropertyChanged(); } }

    private bool _isBusy;
    /// <summary>启动/停止进行中（禁用按钮防并发）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !_isBusy;

    private string _status = "未启动";
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    /// <summary>校验定义完整性（端口范围/目标必填）；动态转发不校验目标。</summary>
    public bool Validate(out string error)
    {
        error = string.Empty;
        if (!TryPort(BindPortText, out _))
        {
            error = "监听端口须为 1-65535";
            return false;
        }
        if (Type != PortForwardType.Dynamic)
        {
            if (string.IsNullOrWhiteSpace(TargetHost))
            {
                error = "目标 Host 不能为空";
                return false;
            }
            if (!TryPort(TargetPortText, out _))
            {
                error = "目标端口须为 1-65535";
                return false;
            }
        }
        return true;
    }

    private static bool TryPort(string text, out int port)
        => int.TryParse(text, out port) && port is >= 1 and <= 65535;

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
