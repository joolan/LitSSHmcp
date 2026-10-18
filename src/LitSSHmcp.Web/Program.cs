using System.Security.Cryptography;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using LitSSHmcp.Web.Sessions;

// Web 宿主把审批通道压到 cli（文件通道）：桌面弹窗在服务端无意义；
// web 通道 M3-5 接入后改 "web,cli"。stdio MCP 子进程经 InheritEnvironmentVariables 继承。
Environment.SetEnvironmentVariable("LITSSH_APPROVAL_CHANNELS", "cli");

// M3-4 授权矩阵（报告 D1 / §10.B.4）：Web 宿主 spawn 的 MCP 子进程强制"用户→目标"白名单+操作级别校验；
// 桌面进程不注入此变量（D1 桌面仅归因不限权）。进程级变量同样由子进程继承。
Environment.SetEnvironmentVariable(HostEnv.AuthzMode, HostEnv.Enforce);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<WebAccountsStore>();
builder.Services.AddSingleton<WebAuthService>();
builder.Services.AddSingleton<WebAccessLog>();
builder.Services.AddSingleton<IConfigService, ConfigService>();
builder.Services.AddSingleton<IContextStore, ContextStore>();
builder.Services.AddSingleton<IWebRuntimeFactory, AgentWebRuntimeFactory>();
builder.Services.AddSingleton<WebSessionManager>();

var app = builder.Build();

await app.Services.GetRequiredService<WebAccountsStore>().InitializeAsync();
await app.Services.GetRequiredService<IContextStore>().InitializeAsync();

app.UseWebSockets();
app.UseMiddleware<WebAuthMiddleware>();
app.UseMiddleware<WebCsrfMiddleware>();

app.MapGet("/health", () => Results.Text("ok"));

app.MapPost("/api/login", async (LoginRequest req, WebAuthService auth, WebAccessLog log, HttpContext ctx) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString();
    var outcome = await auth.LoginAsync(req.Username ?? string.Empty, req.Password ?? string.Empty, ctx.RequestAborted);
    if (outcome.Status == WebLoginStatus.Success && outcome.Token is not null && outcome.User is not null)
    {
        var expires = outcome.ExpiresAtUtc;
        ctx.Response.Cookies.Append(WebAuthExtensions.TokenCookie, outcome.Token,
            WebAuthExtensions.CreateTokenCookieOptions(ctx, expires));
        var csrf = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        ctx.Response.Cookies.Append(WebAuthExtensions.CsrfCookie, csrf,
            WebAuthExtensions.CreateCsrfCookieOptions(ctx, expires));
        log.Write("login_success", outcome.User.Username, ip);
        return Results.Ok(new
        {
            user = new { id = outcome.User.Id, username = outcome.User.Username, role = outcome.User.Role },
            csrfToken = csrf,
            expiresAtUtc = expires
        });
    }
    log.Write(outcome.Status == WebLoginStatus.Locked ? "login_locked" : "login_failure", req.Username, ip, detail: outcome.Status.ToString());
    if (outcome.Status == WebLoginStatus.Locked)
    {
        return Results.Json(
            new { error = "账号已锁定", retryAfterSeconds = (int)(outcome.RetryAfter?.TotalSeconds ?? 0) },
            statusCode: StatusCodes.Status423Locked);
    }
    return Results.Json(new { error = "用户名或密码错误" }, statusCode: StatusCodes.Status401Unauthorized);
});

app.MapPost("/api/logout", async (HttpContext ctx, WebAuthService auth, WebAccessLog log) =>
{
    ctx.Request.Cookies.TryGetValue(WebAuthExtensions.TokenCookie, out var token);
    await auth.LogoutAsync(token, ctx.RequestAborted);
    ctx.Response.Cookies.Delete(WebAuthExtensions.TokenCookie);
    ctx.Response.Cookies.Delete(WebAuthExtensions.CsrfCookie);
    log.Write("logout", ctx.GetWebPrincipal()?.Username, ctx.Connection.RemoteIpAddress?.ToString());
    return Results.Ok();
});

app.MapGet("/api/me", (HttpContext ctx) =>
{
    var principal = ctx.GetWebPrincipal();
    return principal is null
        ? Results.Unauthorized()
        : Results.Ok(new { id = principal.UserId, username = principal.Username, role = principal.Role });
});

app.MapWebSessionEndpoints();

await app.RunAsync();

public sealed record LoginRequest(string? Username, string? Password);

/// <summary>WAF 测试入口锚点（top-level statements 的宿主类）。</summary>
public partial class Program;
