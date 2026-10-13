namespace LitSSHmcp.Core.Models;

/// <summary>远程桌面协议。</summary>
public enum RemoteDesktopProtocol
{
    /// <summary>Windows 远程桌面（默认端口 3389）。</summary>
    Rdp,
    /// <summary>VNC（默认端口 5900）。</summary>
    Vnc
}

/// <summary>
/// 一条远程桌面连接定义（持久化到 config.json 的 remoteDesktops）。
/// 独立功能：与 SSH / 数据源无关，可单独添加、管理并连接；可选经某台已配置的 SSH 服务器建立隧道后连接。
/// 密码仅本机 DPAPI 加密保存、不对 AI 开放。
/// </summary>
public class RemoteDesktopConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    public RemoteDesktopProtocol Protocol { get; set; } = RemoteDesktopProtocol.Rdp;

    /// <summary>目标主机地址（经隧道时由 SSH 服务器代访；直连时本机可达）。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>目标端口（RDP 默认 3389，VNC 默认 5900）。</summary>
    public int Port { get; set; } = 3389;

    /// <summary>登录账号（可选；留空则连接时由客户端弹窗输入）。</summary>
    public string? Username { get; set; }

    /// <summary>登录密码（磁盘上为 DPAPI 密文 enc:；不会返回给 AI）。</summary>
    public string? Password { get; set; }

    /// <summary>Windows 域（RDP 可选，如 CORP）。</summary>
    public string? Domain { get; set; }

    /// <summary>是否自动填充并使用上面的账号密码（关闭则仅预填用户名、由客户端提示输入密码）。</summary>
    public bool UseLoginCredentials { get; set; } = true;

    /// <summary>承载隧道的 SSH 服务器 Id；为空表示直连（不经 SSH 隧道）。</summary>
    public string? TunnelServerId { get; set; }

    /// <summary>RDP 是否全屏启动。</summary>
    public bool Fullscreen { get; set; }

    /// <summary>RDP 指定桌面宽度（像素）；为空表示按显示器/窗口自适应。</summary>
    public int? DesktopWidth { get; set; }

    /// <summary>RDP 指定桌面高度（像素）；为空表示按显示器/窗口自适应。</summary>
    public int? DesktopHeight { get; set; }

    /// <summary>VNC 自定义查看器可执行文件路径；为空则自动探测常见 VNC Viewer。</summary>
    public string? ViewerPath { get; set; }

    /// <summary>分组名（用于列表按分组归类）；空 = 未分组。</summary>
    public string Group { get; set; } = string.Empty;

    public string[] Tags { get; set; } = Array.Empty<string>();
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastConnectedAt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string TagsText => Tags.Length == 0 ? string.Empty : string.Join(", ", Tags);

    [System.Text.Json.Serialization.JsonIgnore]
    public string ProtocolText => Protocol == RemoteDesktopProtocol.Rdp ? "RDP" : "VNC";

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (Host + ":" + Port) : Name;

    /// <summary>RDP 默认端口 3389，VNC 默认端口 5900。</summary>
    public static int DefaultPort(RemoteDesktopProtocol protocol)
        => protocol == RemoteDesktopProtocol.Vnc ? 5900 : 3389;
}
