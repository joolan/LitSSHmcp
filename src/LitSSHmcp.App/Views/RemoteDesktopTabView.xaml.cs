using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.RemoteDesktop;

namespace LitSSHmcp.App.Views;

/// <summary>
/// 一个内嵌远程桌面标签页的承载视图：负责建立/释放 SSH 隧道，并按协议挂载 RDP（ActiveX）或 VNC（WebView2+noVNC）控件。
/// 分离/收回时通过 <see cref="DetachForMove"/> 交出隧道句柄（**不释放隧道**），新视图用 Adopt 构造复用该隧道、仅重建查看器控件，
/// 从而把重连缩到最短。
/// </summary>
public partial class RemoteDesktopTabView : UserControl
{
    private readonly RemoteDesktopSessionViewModel _vm;
    private readonly RemoteDesktopService _service = AppServiceFactory.RemoteDesktopService;

    private string? _forwardId;
    private string? _connectHost;
    private int _connectPort;
    private RdpHostView? _rdp;
    private VncView? _vnc;
    private bool _started;
    private bool _closed;
    private bool _detached;
    private bool _needsRecreate;
    private readonly bool _adopt;   // true = 复用外部交来的隧道（不重新建隧道）

    public RemoteDesktopTabView(RemoteDesktopSessionViewModel vm, string? connectHost = null, int connectPort = 0, string? forwardId = null)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _connectHost = connectHost;
        _connectPort = connectPort;
        _forwardId = forwardId;
        _adopt = connectHost is not null;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_closed || _detached)
            return;

        if (!_started)
        {
            _started = true;
            if (_adopt)
                _ = AttachViewerAsync(_connectHost!, _connectPort);
            else
                _ = ConnectAsync();
            return;
        }

        if (_needsRecreate && _connectHost is not null)
        {
            _needsRecreate = false;
            _ = AttachViewerAsync(_connectHost, _connectPort);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 分离/收回导致的卸载：销毁查看器控件但保留隧道，之后再重建
        if (_closed || _detached || !_started)
            return;

        DisposeViewer();
        _needsRecreate = true;
    }

    private async Task ConnectAsync()
    {
        _vm.Status = "正在建立连接…";
        SetOverlay("正在建立连接…", _vm.Config.ProtocolText + " · " +
            (_vm.TunnelServer is null ? "直连" : $"经 SSH {_vm.TunnelServer.Name}"));

        var (ok, message, tunnel) = await _service.PrepareTunnelAsync(_vm.Config, _vm.TunnelServer);
        if (!ok || tunnel is null)
        {
            _vm.Status = message;
            SetOverlay("连接失败", message);
            return;
        }

        _forwardId = tunnel.ForwardId;
        _connectHost = tunnel.ConnectHost;
        _connectPort = tunnel.ConnectPort;
        if (!await AttachViewerAsync(tunnel.ConnectHost, tunnel.ConnectPort))
        {
            _vm.Status = "连接失败（详见视图提示）";
            await _service.ReleaseTunnelAsync(_forwardId);
            _forwardId = null;
            return;
        }

        await _service.AuditAsync(_vm.Config, _vm.TunnelServer, tunnel.ConnectHost, tunnel.ConnectPort, true, null);
    }

    /// <summary>按目标地址挂载查看器控件。返回控件是否创建成功。</summary>
    private async Task<bool> AttachViewerAsync(string host, int port)
    {
        try
        {
            var via = _vm.TunnelServer is null ? "直连" : $"经 SSH {_vm.TunnelServer.Name}";
            if (_vm.Config.Protocol == RemoteDesktopProtocol.Rdp)
            {
                _rdp = new RdpHostView();
                _rdp.ConnectionStateChanged += OnRdpStateChanged;
                ContentHost.Children.Add(_rdp);
                HideOverlay();
                var width = (int)Math.Max(640, ActualWidth);
                var height = (int)Math.Max(480, ActualHeight);
                if (!_rdp.Connect(_vm.Config, host, port, width, height))
                    return false;
                _vm.Status = $"RDP 连接中…（{via} → {host}:{port}）";
            }
            else
            {
                _vnc = new VncView();
                ContentHost.Children.Add(_vnc);
                HideOverlay();
                if (!await _vnc.ConnectAsync(_vm.Config, host, port))
                    return false;
                _vm.Status = $"VNC 已连接（{via} → {host}:{port}）";
            }
            return true;
        }
        catch (Exception ex)
        {
            _vm.Status = "连接失败: " + ex.Message;
            SetOverlay("连接失败", ex.Message);
            return false;
        }
    }

    private void OnRdpStateChanged(bool connected, string text)
        => _vm.Status = text;

    private void DisposeViewer()
    {
        if (_rdp is not null)
        {
            _rdp.ConnectionStateChanged -= OnRdpStateChanged;
            try { _rdp.Disconnect(); } catch { /* ignore */ }
            ContentHost.Children.Remove(_rdp);
            _rdp = null;
        }
        if (_vnc is not null)
        {
            try { _vnc.Disconnect(); } catch { /* ignore */ }
            ContentHost.Children.Remove(_vnc);
            _vnc = null;
        }
    }

    /// <summary>
    /// 分离/收回时调用：销毁查看器控件、**保留隧道**并交出隧道句柄供新视图复用（<see cref="RemoteDesktopTabView(RemoteDesktopSessionViewModel,string,int,string)"/>）。
    /// 返回 null 表示尚未建立隧道（调用方按需回退为重新连接）。
    /// </summary>
    public (string Host, int Port, string? ForwardId)? DetachForMove()
    {
        if (_closed || _detached)
            return null;

        _detached = true;
        _closed = true;
        DisposeViewer();

        var host = _connectHost;
        var port = _connectPort;
        var fwd = _forwardId;
        _forwardId = null;   // 不释放隧道：交由新视图接管
        return host is not null ? (host, port, fwd) : null;
    }

    /// <summary>关闭该标签页：断开控件并释放隧道。</summary>
    public void CloseSession()
    {
        _closed = true;
        _detached = true;
        DisposeViewer();

        var fwd = _forwardId;
        _forwardId = null;
        _ = _service.ReleaseTunnelAsync(fwd);
    }

    private void SetOverlay(string title, string? detail)
    {
        Overlay.Text = title;
        OverlayDetail.Text = detail ?? string.Empty;
        OverlayHost.Visibility = Visibility.Visible;
        ContentHost.Visibility = Visibility.Collapsed;
    }

    private void HideOverlay()
    {
        OverlayHost.Visibility = Visibility.Collapsed;
        ContentHost.Visibility = Visibility.Visible;
    }
}
