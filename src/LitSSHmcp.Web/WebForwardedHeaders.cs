using System.Net;
using LitSSHmcp.Core.Models;
using Microsoft.AspNetCore.HttpOverrides;

namespace LitSSHmcp.Web;

/// <summary>
/// 显式可信代理（P2-7）：只有 <c>web.trustedProxies</c> 非空时才处理 X-Forwarded-For/Proto，
/// 且只信任列表内代理、只取一跳；为空/非法时完全忽略转发头，避免客户端伪造来源 IP 或 HTTPS 协议。
/// </summary>
public static class WebForwardedHeaders
{
    public static IReadOnlyList<IPAddress> ParseTrustedProxies(IEnumerable<string>? values)
    {
        if (values is null)
            return Array.Empty<IPAddress>();

        var result = new List<IPAddress>();
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (!IPAddress.TryParse(raw.Trim(), out var ip))
                continue;
            if (!result.Contains(ip))
                result.Add(ip);
        }
        return result;
    }

    /// <summary>配置了可信代理时注册 forwarded headers 中间件并返回 true。</summary>
    public static bool TryApply(WebApplication app, WebHostConfig? web)
    {
        var options = CreateOptions(web);
        if (options is null)
            return false;
        app.UseForwardedHeaders(options);
        return true;
    }

    internal static ForwardedHeadersOptions? CreateOptions(WebHostConfig? web)
    {
        var proxies = ParseTrustedProxies(web?.TrustedProxies);
        if (proxies.Count == 0)
            return null;

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        // 默认 KnownNetworks 含 loopback；显式清空后只信任配置列表，避免同机其他进程伪造。
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();
        foreach (var proxy in proxies)
            options.KnownProxies.Add(proxy);
        return options;
    }
}
