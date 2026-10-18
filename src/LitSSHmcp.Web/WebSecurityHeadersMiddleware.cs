namespace LitSSHmcp.Web;

/// <summary>
/// 全站安全响应头（评估报告 §10.B 攻击面收敛/传输加固）：
/// nosniff 防 MIME 嗅探、DENY 防嵌入 iframe 点击劫持、no-referrer 防跨站泄漏、
/// Permissions-Policy 关闭高危特性；HTML 响应另加与 index.html meta 同源的 CSP（双保险，防后续静态页漏写 meta）。
/// </summary>
public sealed class WebSecurityHeadersMiddleware
{
    /// <summary>
    /// 与 wwwroot/index.html 的 meta CSP 保持一致的策略（评估报告 §10.B.3 XSS 防线）。
    /// 改动须两处同步，StaticAssetsTests/SecurityHeadersTests 断言关键子句。
    /// </summary>
    public const string CspPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' https: data:; " +
        "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private readonly RequestDelegate _next;

    public WebSecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var (ctx, _) = ((HttpContext, WebSecurityHeadersMiddleware))state!;
            var headers = ctx.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            var contentType = ctx.Response.ContentType;
            if (contentType is not null &&
                contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                headers["Content-Security-Policy"] = CspPolicy;
            }
            return Task.CompletedTask;
        }, (context, this));
        await _next(context);
    }
}
