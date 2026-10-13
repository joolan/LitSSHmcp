using System.IO;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services.RemoteDesktop;
using LitSSHmcp.Core.Models;
using Microsoft.Web.WebView2.Core;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 内嵌 VNC 视图：用 WebView2 承载随包分发的 <b>noVNC</b> 页面，
/// 连接指向本机 <see cref="VncWebHost"/>（其把 WebSocket 桥接到目标 VNC 的 TCP 端点，可为经 SSH 隧道后的本地端口）。
/// </summary>
public partial class VncView : UserControl
{
    private VncWebHost? _host;
    private bool _webReady;

    public VncView()
    {
        InitializeComponent();
    }

    public async Task<bool> ConnectAsync(RemoteDesktopConfig cfg, string host, int port)
    {
        Disconnect();
        Overlay.Visibility = Visibility.Collapsed;

        try
        {
            if (!_webReady)
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LitSSH", "webview2");
                Directory.CreateDirectory(userDataFolder);
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await Web.EnsureCoreWebView2Async(env);

                var settings = Web.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;
                _webReady = true;
            }

            _host = new VncWebHost(VncWebHost.DefaultRootDir, host, port);
            if (!_host.Start())
                throw new InvalidOperationException("无法启动本机 VNC 代理服务（端口占用？）");

            var password = cfg.UseLoginCredentials ? cfg.Password : null;
            Web.CoreWebView2.Navigate(_host.PageUrl(password));
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Disconnect();
            ShowError("未检测到 Microsoft Edge WebView2 运行时，无法内嵌 VNC。\n" +
                      "请安装 WebView2 Runtime（常随 Edge/Windows 预装），或改用「用外部客户端打开」。");
            return false;
        }
        catch (Exception ex)
        {
            Disconnect();
            ShowError("VNC 连接失败: " + ex.Message);
            return false;
        }
    }

    public void Disconnect()
    {
        try { _host?.Dispose(); } catch { /* ignore */ }
        _host = null;

        try
        {
            if (_webReady && Web.CoreWebView2 is not null)
                Web.CoreWebView2.Navigate("about:blank");
        }
        catch
        {
            // 忽略
        }
    }

    private void ShowError(string message)
    {
        Web.Visibility = Visibility.Collapsed;
        Overlay.Text = message;
        Overlay.Visibility = Visibility.Visible;
    }
}
