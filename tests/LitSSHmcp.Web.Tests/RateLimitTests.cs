using System.Net;
using System.Net.Http.Json;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>登录/API 限速（评估报告 §10.B.1 登录限速 / §10.B.6 API 层限速）。</summary>
public class RateLimitTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void Store_limits_per_bucket_window_and_window_rolls_over()
    {
        var store = new WebRateLimitStore();

        Assert.True(store.TryAcquire("ip1", "login", 2, T0));
        Assert.True(store.TryAcquire("ip1", "login", 2, T0));
        Assert.False(store.TryAcquire("ip1", "login", 2, T0));   // 同窗口第 3 次超限
        Assert.True(store.TryAcquire("ip1", "api", 5, T0));       // 桶间独立
        Assert.True(store.TryAcquire("ip2", "login", 2, T0));     // IP 间独立

        Assert.True(store.TryAcquire("ip1", "login", 2, T0.AddSeconds(61)));   // 新窗口翻转
        Assert.True(store.TryAcquire("ip1", "login", 2, T0.AddSeconds(61)));
        Assert.False(store.TryAcquire("ip1", "login", 2, T0.AddSeconds(61)));
    }

    [Fact]
    public void Store_reset_clears_counts_and_zero_limit_denies()
    {
        var store = new WebRateLimitStore();
        Assert.False(store.TryAcquire("ip", "api", 0, T0));
        store.TryAcquire("ip", "login", 1, T0);
        Assert.False(store.TryAcquire("ip", "login", 1, T0));
        store.Reset();
        Assert.True(store.TryAcquire("ip", "login", 1, T0));
    }

    [Fact]
    public async Task Http_login_api_buckets_429_with_log_and_health_exempt()
    {
        using var fx = new RateLimitFixture();

        // 登录桶（每窗 2 次）
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client.PostAsJsonAsync("/api/login",
            new { username = "nobody", password = "bad" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client.PostAsJsonAsync("/api/login",
            new { username = "nobody", password = "bad" })).StatusCode);
        var blockedLogin = await fx.Client.PostAsJsonAsync("/api/login",
            new { username = "nobody", password = "bad" });
        Assert.Equal(HttpStatusCode.TooManyRequests, blockedLogin.StatusCode);
        Assert.Contains("频繁", await blockedLogin.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // API 桶（每窗 3 次）——401 也消耗配额（防未认证刷接口）
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await fx.Client.GetAsync("/api/sessions")).StatusCode);

        // 静态/健康检查不限速（登录页与探活可用）
        for (var i = 0; i < 8; i++)
            Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/health")).StatusCode);

        Assert.Contains("rate_limited", fx.ReadAccessLog(), StringComparison.Ordinal);
    }

    /// <summary>小阈值策略 + 全新计数器 + 临时访问日志；测试内先 Reset 保证窗口无关。</summary>
    private sealed class RateLimitFixture : IDisposable
    {
        private readonly string _logDir =
            Path.Combine(Path.GetTempPath(), "litssh-ratelimit-" + Guid.NewGuid().ToString("N"));

        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }

        public RateLimitFixture()
        {
            var logDir = _logDir;
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<WebRateLimitPolicy>();
                    services.AddSingleton(new WebRateLimitPolicy { LoginPerMinute = 2, ApiPerMinute = 3 });
                    services.RemoveAll<WebRateLimitStore>();
                    services.AddSingleton(new WebRateLimitStore());
                    services.RemoveAll<WebAccessLog>();
                    services.AddSingleton(new WebAccessLog(logDir));
                }));
            Client = Factory.CreateClient();
            Factory.Services.GetRequiredService<WebRateLimitStore>().Reset();
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

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            try { Directory.Delete(_logDir, recursive: true); } catch { /* 清理失败忽略 */ }
            GC.SuppressFinalize(this);
        }
    }
}
