namespace LitSSHmcp.Core.Models;

/// <summary>端口转发类型。</summary>
public enum PortForwardType
{
    /// <summary>本地转发 (-L)：本机监听，经服务器转发到目标。</summary>
    Local,
    /// <summary>远程转发 (-R)：服务器监听，转发回本机可达的目标。</summary>
    Remote,
    /// <summary>动态转发 (-D)：本机监听为 SOCKS5 代理。</summary>
    Dynamic
}

/// <summary>一条端口转发定义（持久化到 config.json 的 portForwards）。</summary>
public class PortForwardConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    /// <summary>承载转发的 SSH 服务器 Id。</summary>
    public string ServerId { get; set; } = string.Empty;

    public PortForwardType Type { get; set; } = PortForwardType.Local;

    /// <summary>监听地址（本地转发/动态转发为本机，远程转发为服务器侧）。</summary>
    public string BindHost { get; set; } = "127.0.0.1";

    public int BindPort { get; set; }

    /// <summary>目标地址（本地/远程转发用；动态转发忽略）。</summary>
    public string TargetHost { get; set; } = "127.0.0.1";

    public int TargetPort { get; set; }

    /// <summary>打开端口转发窗口时是否自动启动。</summary>
    public bool AutoStart { get; set; }
}
