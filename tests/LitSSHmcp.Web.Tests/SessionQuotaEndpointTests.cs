using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// 每用户并发会话上限的 HTTP 端点行为（评估报告 §10.B.6 配额限速）：
/// 超限创建 409 + 错误消息 + session_limit_reached 访问日志。
/// </summary>
public class SessionQuotaEndpointTests : IClassFixture<SessionQuotaFixture>
{
    private readonly SessionQuotaFixture _fx;

    public SessionQuotaEndpointTests(SessionQuotaFixture fx) => _fx = fx;

    [Fact]
    public async Task Create_beyond_cap_returns_409_and_logs()
    {
        var (token, csrf) = await _fx.LoginAsync("quota-user");
        var before = _fx.ReadAccessLog();

        var first = await _fx.CreateSessionAsync(token, csrf);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _fx.CreateSessionAsync(token, csrf);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("上限", body, StringComparison.Ordinal);

        Assert.Contains("session_limit_reached", _fx.LogAddedSince(before), StringComparison.Ordinal);
    }
}

/// <summary>配置封顶=1（其余与生产一致）+ 临时访问日志。</summary>
public sealed class SessionQuotaFixture : IDisposable
{
    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "litssh-quota-" + Guid.NewGuid().ToString("N"));

    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }

    public SessionQuotaFixture()
    {
        var logDir = _logDir;
        var config = new AppConfig();
        config.Security.Limits.MaxSessionsPerUser = 1;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(services =>
            {
                services.RemoveAll<IConfigService>();
                services.AddSingleton<IConfigService>(new QuotaStubConfig(config));
                services.RemoveAll<WebAccessLog>();
                services.AddSingleton(new WebAccessLog(logDir));
            }));
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public async Task<(string Token, string Csrf)> LoginAsync(string username)
    {
        await Factory.Services.GetRequiredService<WebAccountsStore>()
            .CreateAsync(username, "secret123", iterations: 1000);
        var resp = await Client.PostAsJsonAsync("/api/login", new { username, password = "secret123" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var csrf = body.RootElement.GetProperty("csrfToken").GetString()!;
        var token = resp.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.First(c => c.StartsWith("litssh_token=", StringComparison.Ordinal))
                .Split(';')[0]["litssh_token=".Length..]
            : null;
        return (token!, csrf);
    }

    public async Task<HttpResponseMessage> CreateSessionAsync(string token, string csrf)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/sessions")
        {
            Content = JsonContent.Create(new { title = "配额测试" })
        };
        req.Headers.Add("Cookie", $"litssh_token={token}; litssh_csrf={csrf}");
        req.Headers.Add("X-CSRF-Token", csrf);
        return await Client.SendAsync(req);
    }

    public string ReadAccessLog()
    {
        if (!Directory.Exists(_logDir))
            return string.Empty;
        return string.Join('\n',
            Directory.EnumerateFiles(_logDir, "web-access-*.log")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    public string LogAddedSince(string before)
    {
        var after = ReadAccessLog();
        return after.Length >= before.Length && after.StartsWith(before, StringComparison.Ordinal)
            ? after[before.Length..]
            : after;
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        try { Directory.Delete(_logDir, recursive: true); } catch { /* 清理失败忽略 */ }
        GC.SuppressFinalize(this);
    }

    private sealed class QuotaStubConfig(AppConfig config) : IConfigService
    {
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(config);
        public Task SaveConfigAsync(AppConfig c) => Task.CompletedTask;
        public string GetConfigPath() => "quota-config.json";
    }
}
