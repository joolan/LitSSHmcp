using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.RemoteDesktop;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

/// <summary>远程桌面（RDP / VNC）连接管理页视图模型。</summary>
public class RemoteDesktopManageViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly RemoteDesktopService _service = AppServiceFactory.RemoteDesktopService;
    private readonly IUserDialogService _dialogs;
    private readonly IWindowService _windows;
    private RemoteDesktopConfig? _selected;
    private string _statusMessage = string.Empty;

    public RemoteDesktopManageViewModel(IConfigService configService, IUserDialogService? dialogs = null,
        IWindowService? windows = null)
    {
        _configService = configService;
        _dialogs = dialogs ?? new UserDialogService();
        _windows = windows ?? new WpfWindowService();

        RefreshCommand = new RelayCommand(_ => Load());
        AddCommand = new RelayCommand(_ => Add());
        EditCommand = new RelayCommand(_ => Edit(), _ => Selected != null);
        DeleteCommand = new RelayCommand(_ => Delete(), _ => Selected != null);
        ConnectCommand = new RelayCommand(_ => Connect(), _ => Selected != null);
        OpenExternalCommand = new RelayCommand(_ => OpenExternal(), _ => Selected != null);

        GroupedDesktops = System.Windows.Data.CollectionViewSource.GetDefaultView(Desktops);
        GroupedDesktops.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(RemoteDesktopConfig.Group)));

        _service.StateChanged += OnStateChanged;
        Load();
    }

    public ObservableCollection<RemoteDesktopConfig> Desktops { get; } = new();

    /// <summary>按「分组」字段分组的连接视图。</summary>
    public System.ComponentModel.ICollectionView GroupedDesktops { get; }

    public RemoteDesktopConfig? Selected
    {
        get => _selected;
        set { _selected = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public ICommand RefreshCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand OpenExternalCommand { get; }

    // 内嵌远程桌面会话窗口（多标签；非模态，可同时操作主界面）
    private RemoteDesktopSessionWindow? _sessionWindow;

    private void RaiseCanExecuteChanged()
    {
        (EditCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenExternalCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnStateChanged(string id, bool active, string message)
        => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Selected?.Id == id)
                StatusMessage = message;
        }));

    private void Load() => AsyncOperation.Run("加载远程桌面连接", async () =>
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            Desktops.Clear();
            foreach (var rd in config.RemoteDesktops)
                Desktops.Add(rd);
            StatusMessage = $"已加载 {Desktops.Count} 个远程桌面连接";
        }
        catch (Exception ex)
        {
            StatusMessage = "加载失败: " + ex.Message;
        }
    });

    private void Add() => AsyncOperation.Run("打开新增远程桌面", async () =>
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var window = _windows.CreateRemoteDesktopEditWindow(Application.Current.MainWindow);
            var vm = new RemoteDesktopEditViewModel(_configService, window, config.Servers);
            window.DataContext = vm;
            vm.DialogClosed += (_, result) =>
            {
                window.Close();
                if (result) Load();
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusMessage = "打开添加窗口失败: " + ex.Message;
        }
    });

    private void Edit() => AsyncOperation.Run("打开编辑远程桌面", async () =>
    {
        if (Selected == null) return;
        try
        {
            var config = await _configService.LoadConfigAsync();
            var rd = config.RemoteDesktops.FirstOrDefault(r => r.Id == Selected.Id) ?? Selected;

            var window = _windows.CreateRemoteDesktopEditWindow(Application.Current.MainWindow);
            var vm = new RemoteDesktopEditViewModel(_configService, window, config.Servers, rd);
            window.DataContext = vm;
            vm.DialogClosed += (_, result) =>
            {
                window.Close();
                if (result) Load();
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusMessage = "打开编辑窗口失败: " + ex.Message;
        }
    });

    private void Delete() => AsyncOperation.Run("删除远程桌面", async () =>
    {
        if (Selected == null) return;
        if (!_dialogs.Confirm($"确定删除远程桌面连接 \"{Selected.DisplayName}\" 吗？", "确认删除"))
            return;

        try
        {
            var config = await _configService.LoadConfigAsync();
            config.RemoteDesktops = config.RemoteDesktops.Where(r => r.Id != Selected.Id).ToArray();
            await _configService.SaveConfigAsync(config);
            StatusMessage = $"已删除: {Selected.Name}";
            Selected = null;
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = "删除失败: " + ex.Message;
        }
    });

    private void Connect() => AsyncOperation.Run("连接远程桌面", async () =>
    {
        var (rd, tunnel, error) = await ResolveAsync();
        if (error is not null) { StatusMessage = error; return; }
        if (rd is null) return;

        if (_sessionWindow is { IsLoaded: true })
        {
            _sessionWindow.Activate();
        }
        else
        {
            _sessionWindow = _windows.CreateRemoteDesktopSessionWindow(_configService);
            _sessionWindow.Closed += (_, _) => _sessionWindow = null;
            _sessionWindow.Show();
        }

        _sessionWindow.AddOrActivateSession(rd, tunnel);
        StatusMessage = $"已在内嵌窗口打开 {rd.DisplayName}";
        await TouchLastConnectedAsync(rd.Id);
    });

    private void OpenExternal() => AsyncOperation.Run("用外部客户端打开远程桌面", async () =>
    {
        var (rd, tunnel, error) = await ResolveAsync();
        if (error is not null) { StatusMessage = error; return; }
        if (rd is null) return;

        StatusMessage = $"正在用外部客户端打开 {rd.DisplayName}…";
        var (ok, message) = await _service.ConnectAsync(rd, tunnel);
        StatusMessage = message;
        if (ok)
            await TouchLastConnectedAsync(rd.Id);
    });

    private async Task<(RemoteDesktopConfig? Desktop, SshServerConfig? Tunnel, string? Error)> ResolveAsync()
    {
        if (Selected is null)
            return (null, null, null);

        try
        {
            var config = await _configService.LoadConfigAsync();
            var rd = config.RemoteDesktops.FirstOrDefault(r => r.Id == Selected.Id) ?? Selected;

            SshServerConfig? tunnel = null;
            if (!string.IsNullOrWhiteSpace(rd.TunnelServerId))
            {
                tunnel = config.Servers.FirstOrDefault(s => s.Id == rd.TunnelServerId);
                if (tunnel is null)
                    return (null, null, "隧道服务器不存在或已删除，请编辑连接重新选择。");
                if (tunnel.Disabled)
                    return (null, null, $"隧道服务器 {tunnel.Name} 已禁用，无法建立隧道。");
            }
            return (rd, tunnel, null);
        }
        catch (Exception ex)
        {
            return (null, null, "连接失败: " + ex.Message);
        }
    }

    private async Task TouchLastConnectedAsync(string id)
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var index = Array.FindIndex(config.RemoteDesktops, r => r.Id == id);
            if (index >= 0)
            {
                config.RemoteDesktops[index].LastConnectedAt = DateTime.UtcNow;
                await _configService.SaveConfigAsync(config);
            }
        }
        catch
        {
            // 记录最近连接时间失败不阻断
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
