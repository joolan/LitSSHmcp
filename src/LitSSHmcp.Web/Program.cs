using System.Security.Cryptography;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using LitSSHmcp.Web.Approvals;
using LitSSHmcp.Web.Sessions;

// Web 宿主把审批通道压到 cli（文件通道）：桌面弹窗在服务端无意义；浏览器审批经 WebApprovalHub
// 桥接同一文件通道推送/决策（报告 §3.3.1，无需 web 通道）。stdio MCP 子进程经 InheritEnvironmentVariables 继承。
Environment.SetEnvironmentVariable("LITSSH_APPROVAL_CHANNELS", "cli");

// M3-4 授权矩阵（报告 D1 / §10.B.4）：Web 宿主 spawn 的 MCP 子进程强制"用户→目标"白名单+操作级别校验；
// 桌面进程不注入此变量（D1 桌面仅归因不限权）。进程级变量同样由子进程继承。
Environment.SetEnvironmentVariable(HostEnv.AuthzMode, HostEnv.Enforce);

// 账号管理命令行（M3-1 账号体系 / §3.2）：--create-user <用户名> [--role …]（密码走 LITSSH_NEW_PASSWORD
// 环境变量）、--list-users；未命中则继续起 Web 服务（其余参数照常透传给宿主）。
if (await WebBootstrap.RunCliAsync(args) is { } cliExitCode)
    return cliExitCode;

// 内容根固定到程序目录：否则 content root 跟随启动时工作目录漂移（如从仓库根、
// 任务计划程序、服务方式启动），WebRootPath 指向不存在的 <cwd>\wwwroot → 静态资源 404。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// M3-8 HTTPS（§10.B.2 建议项）：启动即读配置（同一实例随后注入容器，避免双读歧义）；
// 配置了 PFX 证书则追加 HTTPS 监听，默认无证书=仅 HTTP（局域网明文，风险见 README 安全模型）。
// 读失败（如多宿主并发首建 config.json 的瞬时竞争）不阻断启动，按默认配置起宿主，
// 后续各 API 的 LoadConfigAsync 每次重读磁盘，文件就绪后自然恢复。
var bootConfigService = new ConfigService();
AppConfig bootConfig;
try
{
    bootConfig = await bootConfigService.LoadConfigAsync();
}
catch
{
    bootConfig = new AppConfig();
}
var https = WebHttpsBoot.Resolve(bootConfig.Web, File.Exists);
if (https is not null)
{
    var (httpsPort, certPath, certPassword) = https.Value;
    builder.WebHost.ConfigureKestrel(options =>
        options.ListenAnyIP(httpsPort, listen => listen.UseHttps(certPath, certPassword)));
}

// M3-8 / §8.3：Web 会话目录固定根可配（web.jobsRoot，空=默认 %APPDATA%\LitSSH\jobs；改动需重启）
WebSessionManager.ConfigureJobsRoot(bootConfig.Web?.JobsRoot);

builder.Services.AddSingleton<IConfigService>(bootConfigService);
builder.Services.AddSingleton<WebAccountsStore>();
builder.Services.AddSingleton<WebAuthService>();
builder.Services.AddSingleton<WebAccessLog>();
builder.Services.AddSingleton<IContextStore, ContextStore>();
builder.Services.AddSingleton<IWebRuntimeFactory, AgentWebRuntimeFactory>();
builder.Services.AddSingleton<WebSessionManager>();
builder.Services.AddSingleton<WebApprovalHub>();
// M3-8 登录/API 层限速（§10.B.1/B.6）：进程内按 IP 固定窗口计数
builder.Services.AddSingleton<WebRateLimitPolicy>();
builder.Services.AddSingleton<WebRateLimitStore>();

var app = builder.Build();

await app.Services.GetRequiredService<WebAccountsStore>().InitializeAsync();
var webStore = app.Services.GetRequiredService<IContextStore>();
await webStore.InitializeAsync();

// §8.3 布局修正迁移（jobs/jobs → jobs），随后按保留天数清扫孤儿会话目录（§8.2，0=不清扫）
await LegacyJobsLayoutMigration.RunAsync(webStore, WebSessionManager.WebJobsRoot);
try
{
    var aliveSessionIds = (await webStore.ListSessionsAsync()).Select(s => s.Id).ToArray();
    WorkspaceLayout.SweepOrphanSessionDirs(WebSessionManager.WebJobsRoot,
        bootConfig.Agent?.OrphanSessionDirRetentionDays ?? 0, aliveSessionIds);
}
catch
{
    // 清扫失败不阻断启动
}

app.UseWebSockets();
// M3-8 安全响应头（§10.B 攻击面收敛）：nosniff/DENY/no-referrer/Permissions-Policy + HTML CSP
app.UseMiddleware<WebSecurityHeadersMiddleware>();
// M3-8 限速（§10.B.1/B.6）：/api/login 与 /api/* 按 IP 分桶，超限 429 + rate_limited 日志
app.UseMiddleware<WebRateLimitMiddleware>();
// M3-7 SPA（评估报告 §3.1/§10.B.3）：wwwroot 静态前端（登录/会话/流式聊天/审批卡片/目录浏览），
// 与 API 同源；index.html 自带 CSP meta（script-src 'self'，配合 sanitize.js 的构造性 XSS 防护）。
app.UseDefaultFiles();
// 静态资源每次协商（ETag/304）：避免升级后浏览器沿用旧 JS/CSS，出现"修复不生效"的错觉
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});
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

// 模型列表（Web 顶栏展示与按会话切换；仅启用模型）
app.MapGet("/api/agent/providers", async (HttpContext ctx, IConfigService configService) =>
{
    if (ctx.GetWebPrincipal() is null)
        return Results.Unauthorized();
    var config = await configService.LoadConfigAsync();
    var providers = AgentProviderGroupConfig.Flatten(config.Agent.Providers)
        .Where(p => p.Enabled)
        .Select(p => new
        {
            id = p.Id,
            name = p.Name,
            group = p.GroupName,
            model = p.Model,
            display = p.DisplayName
        })
        .ToArray();
    return Results.Ok(new { activeId = config.Agent.ActiveProviderId, providers });
});

app.MapWebSessionEndpoints();
app.MapWebApprovalEndpoints();
app.MapWebFileEndpoints();

// 审批桥就绪即启轮询（§3.3.1：cli 文件通道 → 浏览器推送）；单例首次解析触发后台循环
_ = app.Services.GetRequiredService<WebApprovalHub>();

await app.RunAsync();
return 0;

public sealed record LoginRequest(string? Username, string? Password);

/// <summary>WAF 测试入口锚点（top-level statements 的宿主类）。</summary>
public partial class Program;
