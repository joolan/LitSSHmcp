using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Web;

/// <summary>
/// Web HTTPS 启动解析（评估报告 §10.B.2 建议项）：配置了证书文件才启用，纯函数便于单测；
/// 未配置 = 仅 HTTP（局域网明文，风险须在文档标注，README 安全模型）。
/// </summary>
public static class WebHttpsBoot
{
    public const int DefaultHttpsPort = 7443;

    /// <summary>返回 (端口, 证书路径, 证书密码)；不启用返回 null。</summary>
    public static (int Port, string CertPath, string Password)? Resolve(
        WebHostConfig? web, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var https = web?.Https;
        if (https is null || string.IsNullOrWhiteSpace(https.CertificatePath))
            return null;
        if (!fileExists(https.CertificatePath))
            return null;
        var port = https.Port > 0 ? https.Port : DefaultHttpsPort;
        return (port, https.CertificatePath, https.CertificatePassword ?? string.Empty);
    }
}
