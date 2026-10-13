using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LitSSHmcp.Core.Models;
using Microsoft.Win32;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 内嵌 RDP 视图：用系统自带的 <b>RDP ActiveX 控件</b>（<c>mstscax.dll</c>，通过 <see cref="System.Windows.Forms.AxHost"/> 承载）
/// 在其自身窗口内渲染远程桌面。
/// 说明：① RDP ActiveX **不读取** Windows 凭据管理器（那是 mstsc.exe 的行为），密码须经
/// <see cref="IMsTscNonScriptable"/>（非脚本接口，vtable slot 3）的 <c>ClearTextPassword</c> 写入；
/// ② 非默认端口须经 <c>AdvancedSettings.RDPPort</c> 指定；
/// ③ **分辨率只在连接时确定一次**，之后不再重协商；窗口尺寸变化仅靠 <c>SmartSizing</c> 等比例缩放。
/// </summary>
public partial class RdpHostView : UserControl
{
    private RdpClientAxHost? _ax;
    private dynamic? _ocx;
    private DispatcherTimer? _timer;
    private RemoteDesktopConfig? _cfg;
    private int _lastW;
    private int _lastH;
    private bool? _lastConnected;
    private string _lastText = string.Empty;
    private bool _connectPending;

    /// <summary>连接状态变化（是否已连接 / 状态文本）。由轮询 <c>Connected</c> 触发。</summary>
    public event Action<bool, string>? ConnectionStateChanged;

    public RdpHostView()
    {
        InitializeComponent();
    }

    public bool IsConnected => IsOcxConnected(_ocx);

    /// <summary>安全读取控件的 <c>Connected</c>（其返回 VARIANT_BOOL，晚绑定下可能是 int/bool，避免直接强转布尔）。</summary>
    private static bool IsOcxConnected(object? ocx)
    {
        if (ocx is null)
            return false;
        try { return Convert.ToInt32(((dynamic)ocx).Connected) != 0; }
        catch { return false; }
    }

    public bool Connect(RemoteDesktopConfig cfg, string host, int port, int width, int height)
    {
        Disconnect();
        Overlay.Visibility = Visibility.Collapsed;
        _cfg = cfg;

        try
        {
            var ax = new RdpClientAxHost { Dock = System.Windows.Forms.DockStyle.Fill };
            ((System.ComponentModel.ISupportInitialize)ax).BeginInit();
            FormsHost.Child = ax;
            ((System.ComponentModel.ISupportInitialize)ax).EndInit();
            ax.CreateControl();

            var ocx = ax.OcxObject ?? throw new InvalidOperationException("无法创建 RDP ActiveX 控件（mstscax）");
            _ax = ax;
            _ocx = ocx;
            dynamic o = ocx;

            // 连接地址与端口：ActiveX 的 Server 只认主机名，非默认端口必须经 RDPPort 指定
            o.Server = host;
            Safe(() => o.AdvancedSettings2.RDPPort = port);

            // 账号：支持 "域\用户" 或 "用户" + 单独域；本地账户可用 ".\用户" 或 "机器名\用户"
            var user = cfg.Username;
            var domain = cfg.Domain;
            if (!string.IsNullOrWhiteSpace(user) && user.Contains('\\'))
            {
                var parts = user.Split('\\', 2);
                domain = parts[0];
                user = parts[1];
            }
            if (!string.IsNullOrWhiteSpace(user))
                o.UserName = user;
            if (!string.IsNullOrWhiteSpace(domain))
                o.Domain = domain;

            Safe(() => o.ColorDepth = 32);
            Safe(() => o.AdvancedSettings2.AuthenticationLevel = 2);
            Safe(() => o.ConnectingText = $"正在连接 {host}…");
            ApplyFixedSettings();

            // 密码：写入非脚本接口（ActiveX 不认 Windows 凭据管理器）
            if (cfg.UseLoginCredentials && !string.IsNullOrEmpty(cfg.Password))
                TrySetPassword(ocx, cfg.Password);

            // 等控件完成布局（有实际尺寸）后再按该尺寸连接一次，之后不再重协商
            _connectPending = true;
            SizeChanged += OnViewSizeChanged;
            TryConnectWhenSized();
            Dispatcher.BeginInvoke(new Action(TryConnectWhenSized), DispatcherPriority.Loaded);

            return true;
        }
        catch (Exception ex)
        {
            Disconnect();
            ShowError("RDP 连接失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>与尺寸无关的固定设置（缩放/剪贴板/断线重连/CredSSP 等）。</summary>
    private void ApplyFixedSettings()
    {
        var ocx = _ocx;
        if (ocx is null)
            return;
        dynamic o = ocx;
        // 仅缩放不重协商：会话分辨率固定，窗口尺寸变化靠 SmartSizing 等比例缩放
        Safe(() => o.AdvancedSettings2.SmartSizing = true);
        Safe(() => o.AdvancedSettings.SmartSizing = true);
        Safe(() => o.AdvancedSettings2.DisplayConnectionBar = false);
        Safe(() => o.AdvancedSettings2.RedirectClipboard = true);
        Safe(() => o.AdvancedSettings2.EnableAutoReconnect = true);
        Safe(() => o.AdvancedSettings7.EnableCredSspSupport = true);
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_connectPending)
            TryConnectWhenSized();
        // 已连接后不再重协商分辨率，仅由 SmartSizing 缩放
    }

    private void TryConnectWhenSized()
    {
        var ocx = _ocx;
        if (!_connectPending || ocx is null)
            return;

        var vw = CurrentWidth();
        var vh = CurrentHeight();
        if (vw < 200 || vh < 150)
        {
            // 还没有有效尺寸：保持 pending，等待 SizeChanged（可见/布局完成）后再连
            return;
        }

        _connectPending = false;
        // 会话分辨率取**显示器全尺寸**（物理像素）而非窗口尺寸：这样窗口放大时也有富余，
        // 由 SmartSizing 只做“缩小”适配 → 缩小/放大都能等比填充，且无需重协商。
        var (sw, sh) = TargetSessionSize();
        var w = Math.Clamp(sw, 800, 3840);
        var h = Math.Clamp(sh, 600, 2160);
        try
        {
            ocx.DesktopWidth = w;
            ocx.DesktopHeight = h;
            _lastW = w;
            _lastH = h;
        }
        catch { /* ignore */ }

        ApplyFixedSettings();
        try { ocx.Connect(); } catch { /* 失败时由轮询/错误显示体现 */ }

        try { SizeChanged -= OnViewSizeChanged; } catch { /* ignore */ }
        StartPolling();
    }

    // ---- 物理像素尺寸（WPF 是 DIP，需按 DPI 换算） ----

    private int CurrentWidth()
    {
        double scale = 1.0;
        try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { /* ignore */ }
        var w = (int)Math.Round(ActualWidth * scale);
        if (w < 200)
            w = (int)Math.Round(FormsHost.ActualWidth * scale);
        return w;
    }

    private int CurrentHeight()
    {
        double scale = 1.0;
        try { scale = VisualTreeHelper.GetDpi(this).DpiScaleY; } catch { /* ignore */ }
        var h = (int)Math.Round(ActualHeight * scale);
        if (h < 150)
            h = (int)Math.Round(FormsHost.ActualHeight * scale);
        return h;
    }

    /// <summary>目标会话分辨率（物理像素）：优先用配置里指定的分辨率；否则取显示器全尺寸（且不小于当前窗口），
    /// 作为 SmartSizing 缩小适配的上限，这样窗口放大/缩小都能等比填充、且无需重协商。</summary>
    private (int W, int H) TargetSessionSize()
    {
        // 指定分辨率优先
        if (_cfg is { DesktopWidth: > 0, DesktopHeight: > 0 })
            return (_cfg.DesktopWidth!.Value, _cfg.DesktopHeight!.Value);

        double sx = 1, sy = 1;
        try { var dpi = VisualTreeHelper.GetDpi(this); sx = dpi.DpiScaleX; sy = dpi.DpiScaleY; } catch { /* ignore */ }

        var w = (int)Math.Round(SystemParameters.PrimaryScreenWidth * sx);
        var h = (int)Math.Round(SystemParameters.PrimaryScreenHeight * sy);

        // 至少不小于当前窗口，保证放大时也有富余
        w = Math.Max(w, CurrentWidth());
        h = Math.Max(h, CurrentHeight());
        return (w, h);
    }

    /// <summary>断开并释放 RDP 控件。</summary>
    public void Disconnect()
    {
        try { _timer?.Stop(); } catch { /* ignore */ }
        _timer = null;
        try { SizeChanged -= OnViewSizeChanged; } catch { /* ignore */ }
        _connectPending = false;
        _lastConnected = null;
        _lastText = string.Empty;
        _lastW = 0;
        _lastH = 0;

        var ocx = _ocx;
        _ocx = null;
        try { if (ocx is not null) ocx.Disconnect(); } catch { /* ignore */ }

        try
        {
            FormsHost.Child = null;
            _ax?.Dispose();
        }
        catch
        {
            // 忽略释放异常
        }
        _ax = null;
    }

    // ---- 连接状态轮询（无生成的互操作，无法订阅控件事件） ----

    private void StartPolling()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll();
    }

    private void Poll()
    {
        var ocx = _ocx;
        if (ocx is null)
            return;

        bool connected;
        string text;
        try
        {
            connected = IsOcxConnected(ocx);
            if (connected)
            {
                text = _lastW > 0 ? $"RDP 已连接 {_lastW}×{_lastH}" : "RDP 已连接";
            }
            else
            {
                string? status = null;
                try { status = (string?)ocx.ConnectedStatusText; } catch { /* ignore */ }

                int reason = 0;
                try { reason = Convert.ToInt32(ocx.DisconnectReason); } catch { /* ignore */ }

                if (reason != 0)
                {
                    var desc = string.Empty;
                    try
                    {
                        var ext = Convert.ToInt32(ocx.ExtendedDisconnectReason);
                        desc = (string)ocx.GetErrorDescription((uint)reason, (uint)ext);
                    }
                    catch { /* ignore */ }
                    text = "RDP 已断开" + (string.IsNullOrWhiteSpace(desc) ? string.Empty : "：" + desc);
                }
                else
                {
                    text = string.IsNullOrWhiteSpace(status) ? "RDP 连接中…" : status!;
                }
            }
        }
        catch
        {
            return;
        }

        if (_lastConnected == connected && _lastText == text)
            return;
        _lastConnected = connected;
        _lastText = text;
        ConnectionStateChanged?.Invoke(connected, text);
    }

    private void ShowError(string message)
    {
        Overlay.Text = message;
        Overlay.Visibility = Visibility.Visible;
    }

    private static void Safe(Action action)
    {
        try { action(); } catch { /* 属性/接口不存在：跳过 */ }
    }

    /// <summary>把明文密码写入 RDP 控件的非脚本接口（<c>IMsTscNonScriptable.ClearTextPassword</c>）。</summary>
    private static bool TrySetPassword(object ocx, string password)
    {
        try
        {
            var nonScriptable = (IMsTscNonScriptable)ocx;
            return nonScriptable.SetClearTextPassword(password) == 0;   // S_OK
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// RDP 控件的非脚本接口（IID c1e6743a-…）。方法按 vtable 顺序声明：<c>ClearTextPassword</c> 是
/// 该接口 vtable slot 3（oVft 24）的 <b>PROPERTYPUT</b>。我们只调用它，故仅声明此一个方法即可。
/// </summary>
[ComImport]
[Guid("c1e6743a-41c1-4a74-832a-0dd06c1c7a0e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscNonScriptable
{
    [PreserveSig]
    int SetClearTextPassword([MarshalAs(UnmanagedType.BStr)] string value);
}

/// <summary>
/// 无生成的互操作程序集时，通过运行时 CLSID 承载 RDP ActiveX。
/// 注意：不同 Windows 上注册的 <c>MsTscAx.MsTscAx.*</c> 最高版本不同，且**较新版本可能只登记了 ProgID 却不可创建**
/// （如 .11/.12 在某些机器上 <c>CoCreateInstance</c> 返回 CLASS_E_CLASSNOTAVAILABLE）——因此在运行时**探测首个可创建**的版本。
/// </summary>
internal sealed class RdpClientAxHost : System.Windows.Forms.AxHost
{
    private static readonly string[] CandidateProgIds =
    {
        "MsTscAx.MsTscAx.12", "MsTscAx.MsTscAx.11", "MsTscAx.MsTscAx.10", "MsTscAx.MsTscAx.9",
        "MsTscAx.MsTscAx.8", "MsTscAx.MsTscAx.7", "MsTscAx.MsTscAx.6", "MsTscAx.MsTscAx.5",
        "MsTscAx.MsTscAx.4", "MsTscAx.MsTscAx.3", "MsTscAx.MsTscAx.2", "MsTscAx.MsTscAx.1",
        "MsTscAx.MsTscAx"
    };

    private static string? _resolvedClsid;

    public RdpClientAxHost() : base(ResolveClsid())
    {
    }

    public object? OcxObject => GetOcx();

    private static string ResolveClsid()
    {
        if (_resolvedClsid is not null)
            return _resolvedClsid;

        // 1) 优先：探测哪个 ProgID 能真正创建（能创建才算可用）
        foreach (var progId in CandidateProgIds)
        {
            try
            {
                var type = Type.GetTypeFromProgID(progId);
                if (type is null)
                    continue;
                var probe = Activator.CreateInstance(type);
                if (probe is null)
                    continue;
                var clsid = type.GUID.ToString("B");   // {XXXXXXXX-....}
                try { Marshal.ReleaseComObject(probe); } catch { /* ignore */ }
                _resolvedClsid = clsid;
                return clsid;
            }
            catch
            {
                // 该版本不可创建：试下一个
            }
        }

        // 2) 回退：按注册表 ProgID→CLSID 映射取（不探测）
        foreach (var progId in CandidateProgIds)
        {
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey(progId + "\\CLSID");
                if (key?.GetValue(null) is string clsid && clsid.StartsWith("{", StringComparison.Ordinal))
                {
                    _resolvedClsid = clsid;
                    return clsid;
                }
            }
            catch
            {
                // 忽略注册表访问失败
            }
        }

        _resolvedClsid = "{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}"; // MsTscAx.MsTscAx.10 兜底
        return _resolvedClsid;
    }
}
