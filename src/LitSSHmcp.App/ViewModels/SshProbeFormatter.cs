using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 把 SSH 连通性探测结果转成界面可读文案。
/// 只回 bool 时用户只能看到"连接失败"，无法区分密码错 / 主机密钥变化 / 超时 / 网络不可达。
/// </summary>
public static class SshProbeFormatter
{
    public static string Describe(ConnectionProbeResult result)
    {
        if (result.Success)
            return "连接成功";

        var reason = result.ErrorKind switch
        {
            "auth" => "认证失败（用户名/密码或密钥不正确）",
            "host_key" => "主机密钥不匹配（服务器密钥可能已更换，或存在中间人风险）",
            "timeout" => "连接超时（网络慢、端口被拦截或首次连接过久）",
            "network" => "网络不可达（地址/端口错误或防火墙拦截）",
            _ => "连接失败"
        };

        var detail = string.IsNullOrWhiteSpace(result.Error) ? string.Empty : $"，{result.Error.Trim()}";
        return $"连接失败：{reason}{detail}";
    }
}
