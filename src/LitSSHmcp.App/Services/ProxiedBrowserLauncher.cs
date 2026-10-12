using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 通过 SSH 动态转发（SOCKS5）打开一个“代理浏览器”：浏览器全部 HTTP(S) 流量经 SSH 服务器出口。
/// 使用独立的临时 user-data-dir（否则 Chrome/Edge 单例会把新参数并入已有实例、代理失效），
/// 并禁用 QUIC、强制 WebRTC 走代理以减少真实 IP 泄漏；退出浏览器后由调用方停止转发并清理临时目录。
/// </summary>
public static class ProxiedBrowserLauncher
{
    private static readonly string[] BrowserExes = { "chrome.exe", "msedge.exe", "brave.exe", "chromium.exe" };

    private static readonly string[] CommonPaths =
    {
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
        @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe"
    };

    /// <summary>自动检测 Chromium 系浏览器（Chrome → Edge → Brave → Chromium）；找不到返回 null。</summary>
    public static string? FindChromiumBrowser()
    {
        foreach (var exe in BrowserExes)
        {
            var path = FromAppPaths(exe);
            if (path is not null)
                return path;
        }
        foreach (var path in CommonPaths)
            if (File.Exists(path))
                return path;
        return null;
    }

    private static string? FromAppPaths(string exe)
    {
        foreach (var sub in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\"
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(sub + exe);
                if (key?.GetValue(null) is string path && File.Exists(path))
                    return path;
            }
            catch
            {
                // 忽略注册表访问失败
            }
        }
        return null;
    }

    /// <summary>取一个空闲的本地回环端口（用于 SOCKS 监听）。</summary>
    public static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public sealed record LaunchedBrowser(Process Process, string BaseDir);

    /// <summary>用独立临时 profile 启动浏览器，指向本机 SOCKS 端口，并打开校验页。</summary>
    public static LaunchedBrowser Launch(string browserPath, int socksPort, string serverName)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "litssh-proxy", Guid.NewGuid().ToString("N"));
        var profileDir = Path.Combine(baseDir, "profile");
        Directory.CreateDirectory(profileDir);
        var htmlPath = Path.Combine(baseDir, "proxy-check.html");
        File.WriteAllText(htmlPath, BuildCheckHtml(serverName, socksPort), new UTF8Encoding(false));

        var args = new StringBuilder()
            .Append("--proxy-server=\"socks5://127.0.0.1:").Append(socksPort).Append("\" ")
            .Append("--user-data-dir=\"").Append(profileDir).Append("\" ")
            .Append("--no-first-run --no-default-browser-check ")
            .Append("--disable-quic ")
            .Append("--force-webrtc-ip-handling-policy=disable_non_proxied_udp ")
            .Append('"').Append(htmlPath).Append("\" ")
            .Append("\"https://ipinfo.io/json\"");   // 顶层新标签页直接看出口 IP（iframe 会被跟踪防护拦）

        var psi = new ProcessStartInfo(browserPath) { Arguments = args.ToString(), UseShellExecute = true };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动浏览器进程。");
        return new LaunchedBrowser(process, baseDir);
    }

    /// <summary>尽力删除临时 profile 目录（浏览器可能仍占用文件；失败则忽略）。</summary>
    public static void TryCleanup(string baseDir)
    {
        try
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
        catch
        {
            // 仍被占用：留给系统临时目录清理
        }
    }

    private static string BuildCheckHtml(string serverName, int socksPort)
    {
        var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(serverName) ? "SSH 服务器" : serverName);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>代理校验</title></head>" +
               "<body style=\"font-family:'Microsoft YaHei',sans-serif;margin:24px;color:#222\">" +
               "<h2>本窗口已通过 SSH 服务器代理</h2>" +
               "<p>出口服务器：<b>" + name + "</b>（本机 SOCKS5 <code>127.0.0.1:" + socksPort + "</code>）</p>" +
               "<p>出口 IP（应为你服务器的出口 IP）：<b id=\"ip\">检测中…</b></p>" +
               "<p>已在新标签页打开 <a href=\"https://ipinfo.io/json\" target=\"_blank\">ipinfo.io/json</a>；也可点 " +
               "<a href=\"https://ifconfig.me/ip\" target=\"_blank\">ifconfig.me/ip</a> · " +
               "<a href=\"https://ipinfo.io/ip\" target=\"_blank\">ipinfo.io/ip</a> 核对。<br>" +
               "<span style=\"color:#666\">若显示的仍是本机 IP，说明代理未生效。</span></p>" +
               "<p style=\"color:#a00;margin-top:16px\">提示：SOCKS 覆盖 HTTP/HTTPS；QUIC/WebRTC 已尽量禁用，但仍可能有绕过（插件/原生 socket）。" +
               "需要强隔离请配合系统防火墙封 UDP 或使用 Windows Sandbox/虚拟机。</p>" +
               "<script>(async()=>{const el=document.getElementById('ip');" +
               "const urls=['https://ipinfo.io/json','https://ifconfig.co/json','https://api.ipify.org?format=json'];" +
               "for(const u of urls){try{const r=await fetch(u,{cache:'no-store'});if(!r.ok)continue;const t=await r.text();let ip='';" +
               "try{const j=JSON.parse(t);ip=j.ip||j.ip_addr||j.query||'';}catch{ip=t.trim();}" +
               "if(ip){el.textContent=ip;el.style.color='#0a0';return;}}catch(e){}}" +
               "el.textContent='（自动获取失败，请看新标签页或点上方链接）';})();</script>" +
               "</body></html>";
    }
}
