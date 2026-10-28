using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LitSSHmcp.Web.Tests;

public class WebAlertEndpointTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-alert-{Guid.NewGuid():N}.db");
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), $"litssh-alert-log-{Guid.NewGuid():N}");
    private readonly string _token = "test-alert-token-" + Guid.NewGuid().ToString("N");
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string? _oldToken;

    public async Task InitializeAsync()
    {
        _oldToken = Environment.GetEnvironmentVariable(WebAlertEndpoints.TokenEnvVar);
        Environment.SetEnvironmentVariable(WebAlertEndpoints.TokenEnvVar, _token);
        var config = new FakeWebConfigService();
        config.Config.Servers = new[]
        {
            new SshServerConfig { Id = "s1", Name = "web01", Host = "10.0.0.1" }
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new WebAccessLog(_logDir));
        builder.Services.AddSingleton<IWebMemoryStoreFactory>(new TestStoreFactory(_dbPath));
        builder.Services.AddSingleton<IConfigService>(config);
        _app = builder.Build();
        _app.MapWebAlertEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Environment.SetEnvironmentVariable(WebAlertEndpoints.TokenEnvVar, _oldToken);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* ignore */ }
        try { Directory.Delete(_logDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Firing_alert_creates_team_incident_and_is_idempotent()
    {
        var first = await PostAsync(Alert("firing", "f1"), _token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, firstJson.GetProperty("created").GetInt32());

        var second = await PostAsync(Alert("firing", "f1"), _token);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondJson = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, secondJson.GetProperty("created").GetInt32());
        Assert.Equal(1, secondJson.GetProperty("skipped").GetInt32());

        var store = new AgentMemoryStore(_dbPath);
        var open = await store.ListIncidentsAsync("ssh:s1", "open", 10);
        var incident = Assert.Single(open);
        Assert.Equal("ssh:s1", incident.AssetIds);
        Assert.Contains("磁盘", incident.Title);
    }

    [Fact]
    public async Task Resolved_alert_supersedes_matching_open_incident()
    {
        await PostAsync(Alert("firing", "f2"), _token);
        var response = await PostAsync(Alert("resolved", "f2"), _token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("created").GetInt32());
        var store = new AgentMemoryStore(_dbPath);
        Assert.Empty(await store.ListIncidentsAsync("ssh:s1", "open", 10));
        Assert.Single(await store.ListIncidentsAsync("ssh:s1", "resolved", 10));
    }

    [Fact]
    public async Task Missing_or_wrong_token_is_rejected()
    {
        var missing = await PostAsync(Alert("firing", "f3"), null);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        var wrong = await PostAsync(Alert("firing", "f3"), "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(object payload, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, WebAlertEndpoints.Path)
        {
            Content = JsonContent.Create(payload)
        };
        if (token is not null)
            request.Headers.Add("X-LitSSH-Alert-Token", token);
        return await _client.SendAsync(request);
    }

    private static object Alert(string status, string fingerprint) => new
    {
        version = "4",
        status,
        alerts = new[]
        {
            new
            {
                status,
                fingerprint,
                labels = new Dictionary<string, string>
                {
                    ["alertname"] = "DiskFull",
                    ["hostname"] = "10.0.0.1",
                    ["severity"] = "critical"
                },
                annotations = new Dictionary<string, string>
                {
                    ["summary"] = "磁盘使用率过高",
                    ["description"] = "根分区使用率 95%"
                },
                startsAt = "2026-10-27T00:00:00Z",
                endsAt = status == "resolved" ? "2026-10-27T00:10:00Z" : "0001-01-01T00:00:00Z"
            }
        }
    };

    private sealed class TestStoreFactory(string dbPath) : IWebMemoryStoreFactory
    {
        public IAgentMemoryStore Create(string ownerUserId, bool isAdmin) =>
            new AgentMemoryStore(dbPath, new MemoryAccessScope(ownerUserId, isAdmin));
    }
}
