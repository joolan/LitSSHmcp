using System.Security.Claims;
using LitSSHmcp.Core.Services.Web;

namespace LitSSHmcp.Web;

/// <summary>HttpContext.Items 中主体的键。</summary>
public static class WebAuthItems
{
    public const string Principal = "litssh.web.principal";
}

/// <summary>认证相关扩展。</summary>
public static class WebAuthExtensions
{
    public const string TokenCookie = "litssh_token";
    public const string CsrfCookie = "litssh_csrf";
    public const string CsrfHeader = "X-CSRF-Token";

    /// <summary>当前请求的 Web 主体（未认证返回 null）。</summary>
    public static WebPrincipal? GetWebPrincipal(this HttpContext ctx)
        => ctx.Items.TryGetValue(WebAuthItems.Principal, out var p) ? p as WebPrincipal : null;

    /// <summary>Cookie 选项（HttpOnly 令牌 + SameSite=Strict；Secure 按请求协议对齐）。</summary>
    public static CookieOptions CreateTokenCookieOptions(HttpContext ctx, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = ctx.Request.IsHttps,
        Path = "/",
        Expires = expires
    };

    public static CookieOptions CreateCsrfCookieOptions(HttpContext ctx, DateTimeOffset? expires) => new()
    {
        HttpOnly = false,   // 双提交模式：前端需读出并回填到 X-CSRF-Token
        SameSite = SameSiteMode.Strict,
        Secure = ctx.Request.IsHttps,
        Path = "/",
        Expires = expires
    };
}

/// <summary>
/// 令牌认证中间件：读 litssh_token cookie → WebAuthService 校验（服务端可吊销）→ 注入主体。
/// 无效/过期只清 cookie 并放行（匿名），由端点自行决定 401。
/// </summary>
public sealed class WebAuthMiddleware
{
    private readonly RequestDelegate _next;

    public WebAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, WebAuthService auth)
    {
        if (ctx.Request.Cookies.TryGetValue(WebAuthExtensions.TokenCookie, out var token) && !string.IsNullOrEmpty(token))
        {
            var principal = await auth.ValidateTokenAsync(token, ctx.RequestAborted);
            if (principal is not null)
            {
                ctx.Items[WebAuthItems.Principal] = principal;
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, principal.Username),
                    new Claim(ClaimTypes.Role, principal.Role),
                    new Claim(ClaimTypes.NameIdentifier, principal.UserId.ToString())
                ],
                authenticationType: "web-token"));
            }
            else
            {
                ctx.Response.Cookies.Delete(WebAuthExtensions.TokenCookie);
                ctx.Response.Cookies.Delete(WebAuthExtensions.CsrfCookie);
            }
        }
        await _next(ctx);
    }
}

/// <summary>
/// CSRF 双提交中间件（评估报告 §10.B.1）：变更类方法（POST/PUT/PATCH/DELETE）要求
/// X-CSRF-Token 头 == litssh_csrf cookie（SameSite=Strict 之上的第二道防线）。
/// /api/login 例外（登录前尚无会话令牌）。
/// </summary>
public sealed class WebCsrfMiddleware
{
    private readonly RequestDelegate _next;

    public WebCsrfMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        var mutating = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
        if (mutating && ctx.Request.Path != "/api/login")
        {
            ctx.Request.Cookies.TryGetValue(WebAuthExtensions.CsrfCookie, out var cookie);
            var header = ctx.Request.Headers[WebAuthExtensions.CsrfHeader].ToString();
            if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header)
                || !string.Equals(cookie, header, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "CSRF 校验失败" }, ctx.RequestAborted);
                return;
            }
        }
        await _next(ctx);
    }
}
