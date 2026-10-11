using System.Net;
using System.Net.Sockets;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Trigger;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>简易本地 HTTP 服务器：捕获一次 POST 请求体并返回 200。</summary>
internal sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/hook";
    public string? LastBody { get; private set; }
    public int CallCount { get; private set; }
    public int ResponseStatus { get; set; } = 200;

    public TestHttpServer()
    {
        Port = GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                LastBody = await reader.ReadToEndAsync();
                CallCount++;
                ctx.Response.StatusCode = ResponseStatus;
                var bytes = Encoding.UTF8.GetBytes("ok");
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch
            {
                // listener 关闭
                return;
            }
        }
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* 忽略 */ }
    }
}

public class LocalProcessRunnerTests
{
    [Fact]
    public async Task Captures_stdout_and_exit_code()
    {
        var r = await LocalProcessRunner.RunAsync("cmd.exe", new[] { "/c", "echo hello-trigger" }, 30);
        Assert.True(r.Success);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("hello-trigger", r.Output);
    }

    [Fact]
    public async Task Non_zero_exit_is_failure()
    {
        var r = await LocalProcessRunner.RunAsync("cmd.exe", new[] { "/c", "exit 7" }, 30);
        Assert.False(r.Success);
        Assert.Equal(7, r.ExitCode);
    }

    [Fact]
    public async Task Timeout_kills_process()
    {
        var r = await LocalProcessRunner.RunAsync("cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, timeoutSeconds: 1);
        Assert.False(r.Success);
        Assert.Equal("执行超时", r.Error);
    }
}

public class WebhookSenderTests
{
    [Fact]
    public void DingTalk_sign_matches_hmac_vector()
    {
        // 官方算法：sign = base64(hmac_sha256(secret, timestamp + "\n" + secret))
        var sign = WebhookSender.Sign("1234567890", "SECdemo");
        var expected = Convert.ToBase64String(
            new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes("SECdemo"))
                .ComputeHash(Encoding.UTF8.GetBytes("1234567890\nSECdemo")));
        Assert.Equal(expected, sign);
    }

    [Fact]
    public async Task Generic_template_renders_vars_and_sends_json()
    {
        using var server = new TestHttpServer();
        var action = new TriggerAction
        {
            Type = TriggerActionType.Webhook,
            Url = server.Url,
            WebhookTemplate = TriggerWebhookTemplate.Generic,
            BodyTemplate = "{\"t\":\"{{trigger}}\",\"r\":\"{{result}}\"}"
        };
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["trigger"] = "构建发布", ["result"] = "成功"
        };

        var r = await WebhookSender.SendAsync(new TriggerActionContext
        {
            Trigger = new TriggerConfig { Name = "构建发布" },
            Action = action,
            Vars = vars
        });

        Assert.True(r.Success, r.Error);
        Assert.Equal(200, r.StatusCode);
        Assert.Equal("{\"t\":\"构建发布\",\"r\":\"成功\"}", server.LastBody);
    }

    [Fact]
    public async Task Default_body_is_valid_json_when_template_empty()
    {
        using var server = new TestHttpServer();
        var action = new TriggerAction { Type = TriggerActionType.Webhook, Url = server.Url };
        var r = await WebhookSender.SendAsync(new TriggerActionContext
        {
            Trigger = new TriggerConfig { Name = "T" },
            Action = action,
            Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["trigger"] = "T", ["result"] = "成功", ["cause"] = "手动",
                ["timestamp"] = "2026-10-09 10:00:00", ["steps"] = "1. ✓ git"
            }
        });

        Assert.True(r.Success, r.Error);
        using var doc = System.Text.Json.JsonDocument.Parse(server.LastBody!);
        Assert.Equal("T", doc.RootElement.GetProperty("trigger").GetString());
        Assert.Equal("成功", doc.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Non_2xx_response_is_failure_with_status()
    {
        using var server = new TestHttpServer { ResponseStatus = 500 };
        var action = new TriggerAction { Type = TriggerActionType.Webhook, Url = server.Url };
        var r = await WebhookSender.SendAsync(new TriggerActionContext
        {
            Trigger = new TriggerConfig(), Action = action,
            Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        });
        Assert.False(r.Success);
        Assert.Equal(500, r.StatusCode);
        Assert.Contains("500", r.Error);
    }
}
