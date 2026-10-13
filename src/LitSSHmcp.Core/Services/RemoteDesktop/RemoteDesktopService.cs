using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.RemoteDesktop;

/// <summary>
/// 远程桌面（RDP / VNC）连接服务：
/// ① 可选经某台已配置的 SSH 服务器建立本地隧道（-L 127.0.0.1:&lt;空闲端口&gt; → 目标:端口），
///    从而访问「只在堡垒机后可达」的内网桌面；直连模式则本机直连目标；
/// ② 用系统自带 <c>mstsc.exe</c>（RDP）或探测到的 VNC Viewer 打开连接；
/// ③ 连接写入审计；查看器退出后自动拆除隧道。
/// 说明：本阶段为「外部客户端」模式（内嵌 RDP/VNC 视图后续再做）；密码仅在需要时经 Windows 凭据管理器
/// （cmdkey）暂存供 mstsc 使用，不写入 .rdp 明文文件。
/// </summary>
public sealed class RemoteDesktopService : IDisposable
{
    private readonly PortForwardService _portForwards;
    private readonly IAuditLogService? _audit;
    private readonly Dictionary<string, LiveSession> _live = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _disposed;

    /// <summary>连接状态变化（查看器退出 / 隧道拆除）。可能在线程池线程触发，订阅方自行调度到 UI 线程。</summary>
    public event Action<string, bool, string>? StateChanged;

    public RemoteDesktopService(PortForwardService portForwards, IAuditLogService? audit = null)
    {
        _portForwards = portForwards ?? throw new ArgumentNullException(nameof(portForwards));
        _audit = audit;
    }

    public bool IsActive(string id)
    {
        lock (_gate)
            return _live.ContainsKey(id);
    }

    /// <summary>隧道就绪结果：连接应指向 <see cref="ConnectHost"/>:<see cref="ConnectPort"/>；<see cref="ForwardId"/> 非空时用完后须 <see cref="ReleaseTunnelAsync"/>。</summary>
    public sealed record PreparedTunnel(string ConnectHost, int ConnectPort, string? ForwardId);

    /// <summary>
    /// 为一次连接准备隧道：<paramref name="tunnelServer"/> 为空则直连（ConnectHost=目标主机）；
    /// 非空则建立本地 SSH 隧道（127.0.0.1 随机端口 → 目标）。调用方用完后须释放非空 ForwardId。
    /// </summary>
    public async Task<(bool Success, string Message, PreparedTunnel? Tunnel)> PrepareTunnelAsync(RemoteDesktopConfig cfg, SshServerConfig? tunnelServer)
    {
        if (cfg is null)
            return (false, "连接定义为空", null);
        if (string.IsNullOrWhiteSpace(cfg.Host))
            return (false, "目标主机地址不能为空", null);

        if (tunnelServer is null)
            return (true, string.Empty, new PreparedTunnel(cfg.Host, cfg.Port, null));

        var port = GetFreeLoopbackPort();
        var forwardId = Guid.NewGuid().ToString("N");
        var forward = new PortForwardConfig
        {
            Id = forwardId,
            Name = $"远程桌面 · {cfg.DisplayName}",
            ServerId = tunnelServer.Id,
            Type = PortForwardType.Local,
            BindHost = "127.0.0.1",
            BindPort = port,
            TargetHost = cfg.Host,
            TargetPort = cfg.Port,
            AutoStart = false
        };

        var (ok, message) = await _portForwards.StartAsync(tunnelServer, forward).ConfigureAwait(false);
        return ok
            ? (true, string.Empty, new PreparedTunnel("127.0.0.1", port, forwardId))
            : (false, "建立 SSH 隧道失败: " + message, null);
    }

    /// <summary>释放 <see cref="PrepareTunnelAsync"/> 返回的隧道（ForwardId 为空则无操作）。</summary>
    public async Task ReleaseTunnelAsync(string? forwardId)
    {
        if (string.IsNullOrEmpty(forwardId))
            return;
        try { await _portForwards.StopAsync(forwardId).ConfigureAwait(false); } catch { /* ignore */ }
    }

    /// <summary>
    /// 发起一次远程桌面连接（**外部客户端**模式：调用系统 mstsc / 已安装的 VNC 查看器）。
    /// <paramref name="tunnelServer"/> 非空时先建隧道再连接其本地端口；查看器退出后自动拆除隧道并广播状态。
    /// 内嵌视图请改用 <see cref="PrepareTunnelAsync"/> + 自绘控件。
    /// </summary>
    public async Task<(bool Success, string Message)> ConnectAsync(RemoteDesktopConfig cfg, SshServerConfig? tunnelServer)
    {
        if (_disposed != 0)
            return (false, "服务已释放");

        var (ok, prepMsg, tunnel) = await PrepareTunnelAsync(cfg, tunnelServer).ConfigureAwait(false);
        if (!ok || tunnel is null)
            return (false, prepMsg);

        var connectHost = tunnel.ConnectHost;
        var connectPort = tunnel.ConnectPort;
        var forwardId = tunnel.ForwardId;

        // 启动查看器（放后台线程：RDP 的 cmdkey 凭据暂存可能短暂阻塞）
        try
        {
            var launched = await Task.Run(() => cfg!.Protocol == RemoteDesktopProtocol.Rdp
                ? LaunchRdp(cfg!, connectHost, connectPort)
                : LaunchVnc(cfg!, connectHost, connectPort)).ConfigureAwait(false);

            var session = new LiveSession(cfg!.Id, forwardId, launched.Process, launched.BaseDir, launched.Target);
            lock (_gate)
            {
                // 同一 id 已在运行：先停掉旧隧道引用，避免句柄泄漏
                if (_live.TryGetValue(cfg.Id, out var old) && old.ForwardId is { } oldFwd && oldFwd != forwardId)
                    _ = _portForwards.StopAsync(oldFwd);
                _live[cfg.Id] = session;
            }

            HookExit(session);

            await AuditAsync(cfg, tunnelServer, connectHost, connectPort, success: true, detail: null).ConfigureAwait(false);
            var via = tunnelServer is null ? "直连" : $"经 SSH {tunnelServer.Name}";
            return (true, $"已打开 {cfg.ProtocolText}：{cfg.DisplayName}（{via} → {connectHost}:{connectPort}）");
        }
        catch (Exception ex)
        {
            await ReleaseTunnelAsync(forwardId).ConfigureAwait(false);
            await AuditAsync(cfg!, tunnelServer, connectHost, connectPort, success: false, detail: ex.Message).ConfigureAwait(false);
            return (false, "启动失败: " + ex.Message);
        }
    }

    /// <summary>主动断开：拆除隧道（查看器由用户自行关闭）。</summary>
    public async Task DisconnectAsync(string id)
    {
        LiveSession? session;
        lock (_gate)
        {
            if (!_live.TryGetValue(id, out session))
                return;
            _live.Remove(id);
        }

        if (session.ForwardId is { } fwd)
        {
            try { await _portForwards.StopAsync(fwd).ConfigureAwait(false); } catch { /* ignore */ }
        }
        TryCleanup(session.BaseDir);
        StateChanged?.Invoke(id, false, "已断开");
    }

    private void HookExit(LiveSession session)
    {
        var process = session.Process;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnViewerExited(session);
        try
        {
            // 查看器可能在订阅前就已退出（启动即失败）——补一次清理
            if (process.HasExited)
                OnViewerExited(session);
        }
        catch
        {
            // HasExited 偶发异常忽略
        }
    }

    private void OnViewerExited(LiveSession session)
    {
        if (Interlocked.Exchange(ref session.Exited, 1) == 1)
            return;

        lock (_gate)
        {
            if (_live.TryGetValue(session.Id, out var cur) && ReferenceEquals(cur, session))
                _live.Remove(session.Id);
        }

        if (session.ForwardId is { } fwd)
        {
            // 查看器关闭后拆除隧道（不阻塞退出回调）
            _ = Task.Run(async () =>
            {
                try { await _portForwards.StopAsync(fwd).ConfigureAwait(false); } catch { /* ignore */ }
            });
        }
        TryCleanup(session.BaseDir);
        StateChanged?.Invoke(session.Id, false, "连接已关闭");
    }

    // ---- RDP ----

    private sealed record Launched(Process Process, string? BaseDir, string Target);

    private Launched LaunchRdp(RemoteDesktopConfig cfg, string host, int port)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("RDP 仅支持 Windows");

        var baseDir = Path.Combine(Path.GetTempPath(), "litssh-rdp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var rdpFile = Path.Combine(baseDir, "session.rdp");
        File.WriteAllText(rdpFile, BuildRdp(cfg, host, port), new UTF8Encoding(false));

        // 需要自动登录时，把凭据暂存到 Windows 凭据管理器（mstsc 会按 TERMSRV/<host> 取用）。
        // 注意：密码会短暂出现在 cmdkey 命令行参数中（本机当前用户进程）；不写入 .rdp 明文文件。
        if (cfg.UseLoginCredentials && !string.IsNullOrEmpty(cfg.Username))
            StoreCredential(cfg, host);

        var mstsc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe");
        if (!File.Exists(mstsc))
            mstsc = "mstsc.exe";

        var psi = new ProcessStartInfo(mstsc) { Arguments = $"\"{rdpFile}\"", UseShellExecute = true };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 mstsc.exe");
        return new Launched(process, baseDir, $"{host}:{port}");
    }

    private static string BuildRdp(RemoteDesktopConfig cfg, string host, int port)
    {
        var sb = new StringBuilder();
        sb.Append("full address:s:").Append(host).Append(':').Append(port).AppendLine();
        sb.Append("screen mode id:i:").Append(cfg.Fullscreen ? 2 : 1).AppendLine();
        if (!cfg.Fullscreen)
        {
            sb.Append("desktopwidth:i:").Append(cfg.DesktopWidth is > 0 ? cfg.DesktopWidth.Value : 1280).AppendLine();
            sb.Append("desktopheight:i:").Append(cfg.DesktopHeight is > 0 ? cfg.DesktopHeight.Value : 800).AppendLine();
        }
        sb.AppendLine("use multimon:i:0");
        sb.AppendLine("session bpp:i:32");
        sb.AppendLine("compression:i:1");
        sb.AppendLine("keyboardhook:i:2");
        sb.AppendLine("connection type:i:7");
        sb.AppendLine("networkautodetect:i:1");
        sb.AppendLine("bandwidthautodetect:i:1");
        sb.AppendLine("displayconnectionbar:i:1");
        sb.AppendLine("allow font smoothing:i:1");
        sb.AppendLine("allow desktop composition:i:1");
        sb.AppendLine("disable wallpaper:i:0");
        sb.AppendLine("disable full window drag:i:1");
        sb.AppendLine("disable menu anims:i:1");
        sb.AppendLine("disable themes:i:0");
        sb.AppendLine("bitmapcachepersistenable:i:1");
        sb.AppendLine("audiomode:i:0");
        sb.AppendLine("redirectprinters:i:0");
        sb.AppendLine("redirectcomports:i:0");
        sb.AppendLine("redirectsmartcards:i:0");
        sb.AppendLine("redirectclipboard:i:1");
        sb.AppendLine("autoreconnection enabled:i:1");
        sb.AppendLine("authentication level:i:2");
        sb.AppendLine("negotiate security layer:i:1");
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine("use redirection server name:i:0");

        if (cfg.UseLoginCredentials && !string.IsNullOrEmpty(cfg.Username))
            sb.Append("username:s:").Append(cfg.Username).AppendLine();
        if (!string.IsNullOrWhiteSpace(cfg.Domain))
            sb.Append("domain:s:").Append(cfg.Domain).AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// 把连接的用户名/密码暂存到 Windows 凭据管理器（mstsc / RDP ActiveX 会按 <c>TERMSRV/&lt;host&gt;</c> 取用）。
    /// 注意：密码会短暂出现在本机 cmdkey 命令行参数中；不写入 .rdp 明文文件。失败不抛出。
    /// </summary>
    public static void StoreCredential(RemoteDesktopConfig cfg, string host)
    {
        try
        {
            var user = cfg.Username ?? string.Empty;
            // 用户名字段可能已含 "域\用户"；否则在配置了域时补上域前缀
            if (!user.Contains('\\') && !string.IsNullOrWhiteSpace(cfg.Domain))
                user = cfg.Domain + "\\" + user;
            var psi = new ProcessStartInfo("cmdkey")
            {
                Arguments = $"/generic:TERMSRV/{host} /user:\"{user}\" /pass:\"{cfg.Password ?? string.Empty}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch
        {
            // 凭据暂存失败不阻断连接：mstsc 届时会弹窗要求输入密码
        }
    }

    // ---- VNC ----

    private Launched LaunchVnc(RemoteDesktopConfig cfg, string host, int port)
    {
        var viewer = ResolveVncViewer(cfg.ViewerPath);
        if (viewer is null)
            throw new InvalidOperationException(
                "未找到 VNC 查看器。请在连接定义中填写 VNC 查看器路径（如 TigerVNC / TightVNC / RealVNC / UltraVNC 的 vncviewer.exe）。");

        // 多数 VNC 查看器（TigerVNC / TightVNC / RealVNC / UltraVNC）支持 host::port 指定端口
        var target = $"{host}::{port}";
        var psi = new ProcessStartInfo(viewer) { Arguments = target, UseShellExecute = true };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 VNC 查看器");
        return new Launched(process, null, target);
    }

    private static string? ResolveVncViewer(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var candidates = new[]
        {
            @"C:\Program Files\TigerVNC\vncviewer.exe",
            @"C:\Program Files\RealVNC\VNC Viewer\vncviewer.exe",
            @"C:\Program Files\TightVNC\tvnviewer.exe",
            @"C:\Program Files (x86)\TightVNC\tvnviewer.exe",
            @"C:\Program Files\UltraVNC\vncviewer.exe",
            @"C:\Program Files (x86)\UltraVNC\vncviewer.exe"
        };
        foreach (var path in candidates)
            if (File.Exists(path))
                return path;

        foreach (var exe in new[] { "vncviewer.exe", "tvnviewer.exe" })
        {
            var fromRegistry = FromAppPaths(exe);
            if (fromRegistry is not null)
                return fromRegistry;
        }
        return null;
    }

    private static string? FromAppPaths(string exe)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        foreach (var sub in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\"
                 })
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(sub + exe);
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

    // ---- 辅助 ----

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static void TryCleanup(string? baseDir)
    {
        if (string.IsNullOrEmpty(baseDir))
            return;
        try
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
        catch
        {
            // 查看器可能仍占用文件：交给系统临时目录清理
        }
    }

    public async Task AuditAsync(RemoteDesktopConfig cfg, SshServerConfig? tunnelServer, string host, int port, bool success, string? detail)
    {
        if (_audit is null)
            return;
        try
        {
            await _audit.InitializeAsync().ConfigureAwait(false);
            var via = tunnelServer is null ? "直连" : $"经 SSH {tunnelServer.Name}({tunnelServer.Host})";
            var log = new CommandAuditLog
            {
                ServerId = tunnelServer?.Id ?? cfg.Id,
                ServerName = tunnelServer?.Name ?? cfg.Name,
                Command = $"{cfg.ProtocolText} 连接 → {host}:{port}（{via}）源 {cfg.Host}:{cfg.Port}",
                Status = success ? CommandStatus.Executed : CommandStatus.Failed,
                Category = AuditCategory.Exec,
                Tool = "remote_desktop",
                Result = detail
            };
            await _audit.LogCommandAsync(log).ConfigureAwait(false);
        }
        catch
        {
            // 审计失败不阻断连接
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        string[] forwardIds;
        lock (_gate)
        {
            forwardIds = _live.Values
                .Where(s => s.ForwardId is not null)
                .Select(s => s.ForwardId!)
                .ToArray();
            _live.Clear();
        }

        foreach (var forwardId in forwardIds)
        {
            try { _portForwards.StopAsync(forwardId).GetAwaiter().GetResult(); } catch { /* ignore */ }
        }
    }

    private sealed class LiveSession
    {
        public LiveSession(string id, string? forwardId, Process process, string? baseDir, string target)
        {
            Id = id;
            ForwardId = forwardId;
            Process = process;
            BaseDir = baseDir;
            Target = target;
        }

        public string Id { get; }
        public string? ForwardId { get; }
        public Process Process { get; }
        public string? BaseDir { get; }
        public string Target { get; }
        public int Exited;
    }
}
