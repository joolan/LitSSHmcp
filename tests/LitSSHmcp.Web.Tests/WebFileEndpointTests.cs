using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// 会话目录文件浏览/下载集成测试（报告 D6/§10.C.2-3/§10.A.3）：
/// 认证、owner 隔离 404、路径逃逸（../、junction）检出、下载字节与附件头、file_denied/file_download 落盘。
/// 访问日志换临时目录实例，断言确定。
/// </summary>
public class WebFileEndpointTests : IClassFixture<FileFixture>
{
    private readonly FileFixture _fx;

    public WebFileEndpointTests(FileFixture fx) => _fx = fx;

    [Fact]
    public async Task List_requires_authentication()
    {
        var resp = await _fx.Client.GetAsync("/api/sessions/1/files");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task List_root_shows_files_and_dirs_subdir_lists_children()
    {
        var (token, csrf) = await _fx.LoginAsync("file-owner");
        var (id, dir) = await _fx.CreateSessionAsync(token, csrf);
        try
        {
            File.WriteAllText(Path.Combine(dir, "report.md"), "hello");
            Directory.CreateDirectory(Path.Combine(dir, "out"));
            File.WriteAllText(Path.Combine(dir, "out", "x.txt"), "x");

            var root = await ListAsync(token, id);
            var byName = root.ToDictionary(e => e.GetProperty("name").GetString()!, e => e.Clone());
            Assert.True(byName.ContainsKey("report.md"));
            Assert.False(byName["report.md"].GetProperty("isDir").GetBoolean());
            Assert.Equal(5, byName["report.md"].GetProperty("size").GetInt64());
            Assert.True(byName["out"].GetProperty("isDir").GetBoolean());
            // 非递归：子目录内容不在根列表
            Assert.DoesNotContain(root, e => e.GetProperty("path").GetString() == "out/x.txt");

            var sub = await ListAsync(token, id, "out");
            var child = Assert.Single(sub);
            Assert.Equal("out/x.txt", child.GetProperty("path").GetString());
            Assert.False(child.GetProperty("isDir").GetBoolean());
        }
        finally
        {
            await DeleteSessionAsync(token, csrf, id);
        }
    }

    [Fact]
    public async Task List_foreign_session_404_and_logs_denied()
    {
        var (aliceToken, aliceCsrf) = await _fx.LoginAsync("file-alice");
        var (aliceId, _) = await _fx.CreateSessionAsync(aliceToken, aliceCsrf);
        var (bobToken, _) = await _fx.LoginAsync("file-bob");
        var before = _fx.ReadAccessLog();

        var resp = await _fx.Client.SendAsync(Authed(HttpMethod.Get, $"/api/sessions/{aliceId}/files", bobToken, null));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("file_denied", _fx.LogAddedSince(before), StringComparison.Ordinal);
        await DeleteSessionAsync(aliceToken, aliceCsrf, aliceId);
    }

    [Fact]
    public async Task List_traversal_denied_and_logged()
    {
        var (token, csrf) = await _fx.LoginAsync("file-trav");
        var (id, dir) = await _fx.CreateSessionAsync(token, csrf);
        var before = _fx.ReadAccessLog();
        try
        {
            // 框架先 URL 解码（%2f→/），处理器见 ".." 即拒绝（§10.C.2 解码后再校验）
            var resp = await _fx.Client.SendAsync(Authed(HttpMethod.Get,
                $"/api/sessions/{id}/files?path=" + Uri.EscapeDataString("../escape.txt"), token, null));

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Contains("file_denied", _fx.LogAddedSince(before), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "escape.txt")));
        }
        finally
        {
            await DeleteSessionAsync(token, csrf, id);
        }
    }

    [Fact]
    public async Task Download_returns_bytes_with_attachment_header_and_logs()
    {
        var (token, csrf) = await _fx.LoginAsync("file-dl");
        var (id, dir) = await _fx.CreateSessionAsync(token, csrf);
        var before = _fx.ReadAccessLog();
        try
        {
            var payload = "M3-6 deliverable payload"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(dir, "deliverable.txt"), payload);

            var resp = await _fx.Client.SendAsync(Authed(HttpMethod.Get,
                $"/api/sessions/{id}/download?path=deliverable.txt", token, null));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(payload, await resp.Content.ReadAsByteArrayAsync());
            Assert.Equal((long)payload.Length, resp.Content.Headers.ContentLength);
            Assert.True(resp.Headers.TryGetValues("Accept-Ranges", out var acceptRanges));
            Assert.Equal("bytes", Assert.Single(acceptRanges!));
            var disposition = resp.Content.Headers.ContentDisposition;
            Assert.NotNull(disposition);
            Assert.Contains("deliverable.txt", disposition!.FileName ?? disposition.FileNameStar, StringComparison.Ordinal);
            Assert.Contains("file_download", _fx.LogAddedSince(before), StringComparison.Ordinal);
        }
        finally
        {
            await DeleteSessionAsync(token, csrf, id);
        }
    }

    [Fact]
    public async Task Download_supports_single_range_and_rejects_invalid_or_multi_range()
    {
        var (token, csrf) = await _fx.LoginAsync("file-dl-range");
        var (id, dir) = await _fx.CreateSessionAsync(token, csrf);
        try
        {
            var payload = "0123456789"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(dir, "range.txt"), payload);

            var mid = await DownloadAsync(token, id, "range.txt", new RangeHeaderValue(2, 5));
            Assert.Equal(HttpStatusCode.PartialContent, mid.StatusCode);
            Assert.Equal("2345", await mid.Content.ReadAsStringAsync());
            Assert.Equal(4L, mid.Content.Headers.ContentLength);
            Assert.Equal(2L, mid.Content.Headers.ContentRange!.From!.Value);
            Assert.Equal(5L, mid.Content.Headers.ContentRange.To!.Value);
            Assert.Equal(10L, mid.Content.Headers.ContentRange.Length!.Value);

            var suffix = await DownloadAsync(token, id, "range.txt", new RangeHeaderValue(null, 3));
            Assert.Equal(HttpStatusCode.PartialContent, suffix.StatusCode);
            Assert.Equal("789", await suffix.Content.ReadAsStringAsync());

            var openEnded = await DownloadAsync(token, id, "range.txt", new RangeHeaderValue(7, null));
            Assert.Equal(HttpStatusCode.PartialContent, openEnded.StatusCode);
            Assert.Equal("789", await openEnded.Content.ReadAsStringAsync());

            var beyond = await DownloadWithRangeAsync(token, id, "range.txt", "bytes=20-");
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.StatusCode);
            Assert.Equal("bytes */10", beyond.Content.Headers.ContentRange!.ToString());

            var multi = await DownloadWithRangeAsync(token, id, "range.txt", "bytes=0-1,3-4");
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, multi.StatusCode);
        }
        finally
        {
            await DeleteSessionAsync(token, csrf, id);
        }
    }

    [Fact]
    public async Task Download_rejects_file_over_limit_with_413_and_logs()
    {
        var (token, csrf) = await _fx.LoginAsync("file-dl-big");
        var (id, dir) = await _fx.CreateSessionAsync(token, csrf);
        var before = _fx.ReadAccessLog();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "big.bin"), new byte[1025]);

            var resp = await _fx.Client.SendAsync(Authed(HttpMethod.Get,
                $"/api/sessions/{id}/download?path=big.bin", token, null));

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("上限", body, StringComparison.Ordinal);
            Assert.Contains("file_download_too_large", _fx.LogAddedSince(before), StringComparison.Ordinal);
        }
        finally
        {
            await DeleteSessionAsync(token, csrf, id);
        }
    }

    [Fact]
    public void Download_limiter_enforces_per_user_cap()
    {
        var limiter = new WebDownloadLimiter();

        using var first = limiter.TryAcquire("user:1", 1);
        Assert.NotNull(first);
        Assert.Null(limiter.TryAcquire("user:1", 1));
        Assert.Null(limiter.TryAcquire("user:3", 0));

        using var other = limiter.TryAcquire("user:2", 1);
        Assert.NotNull(other);
    }

    [Fact]
    public async Task Download_foreign_session_404()
    {
        var (aliceToken, aliceCsrf) = await _fx.LoginAsync("file-dl-alice");
        var (aliceId, aliceDir) = await _fx.CreateSessionAsync(aliceToken, aliceCsrf);
        await File.WriteAllTextAsync(Path.Combine(aliceDir, "secret.txt"), "secret");
        var (bobToken, _) = await _fx.LoginAsync("file-dl-bob");

        var resp = await _fx.Client.SendAsync(Authed(HttpMethod.Get,
            $"/api/sessions/{aliceId}/download?path=secret.txt", bobToken, null));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        await DeleteSessionAsync(aliceToken, aliceCsrf, aliceId);
    }

    [Fact]
    public async Task Download_missing_file_or_directory_404_without_denied_log()
    {
        var (token, csrf) = await _fx.LoginAsync("file-dl-miss");
        var (id, dir) = await _fx.CreateSessionAsync(token, csrf);
        var before = _fx.ReadAccessLog();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "folder"));

            var missing = await _fx.Client.SendAsync(Authed(HttpMethod.Get,
                $"/api/sessions/{id}/download?path=absent.txt", token, null));
            var isDir = await _fx.Client.SendAsync(Authed(HttpMethod.Get,
                $"/api/sessions/{id}/download?path=folder", token, null));
            var noPath = await _fx.Client.SendAsync(Authed(HttpMethod.Get,
                $"/api/sessions/{id}/download", token, null));

            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, isDir.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, noPath.StatusCode);
            // 正常缺失不是越权，不应记 file_denied
            Assert.DoesNotContain("file_denied", _fx.LogAddedSince(before), StringComparison.Ordinal);
        }
        finally
        {
            await DeleteSessionAsync(token, csrf, id);
        }
    }

    /// <summary>
    /// §10.C.3：会话目录内放指向外部的 junction，GetFullPath 前缀检查会通过但实际落点在外面——
    /// PathGuard 物理解析必须检出（404 + file_denied），且根列表不展示该链接条目。
    /// </summary>
    [Fact]
    public async Task Download_through_junction_escaped_to_outside_denied()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (token, csrf) = await _fx.LoginAsync("file-junction");
        var (id, dir) = await _fx.CreateSessionAsync(token, csrf);
        var before = _fx.ReadAccessLog();
        var outside = Path.Combine(Path.GetTempPath(), "litssh-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "escaped");
            if (!TryCreateDirLink(Path.Combine(dir, "leak"), outside))
                return;     // 创建链接失败（权限/平台）→ 跳过

            var throughLink = await _fx.Client.SendAsync(Authed(HttpMethod.Get,
                $"/api/sessions/{id}/download?path=leak/secret.txt", token, null));
            Assert.Equal(HttpStatusCode.NotFound, throughLink.StatusCode);
            Assert.Contains("file_denied", _fx.LogAddedSince(before), StringComparison.Ordinal);

            var sub = await _fx.Client.SendAsync(Authed(HttpMethod.Get,
                $"/api/sessions/{id}/files?path=leak", token, null));
            Assert.Equal(HttpStatusCode.NotFound, sub.StatusCode);

            // 根列表跳过 reparse point 条目
            var root = await ListAsync(token, id);
            Assert.DoesNotContain(root, e => e.GetProperty("name").GetString() == "leak");
        }
        finally
        {
            await DeleteSessionAsync(token, csrf, id);
            try { Directory.Delete(outside, recursive: true); } catch { /* ignore */ }
        }
    }

    private async Task<JsonElement[]> ListAsync(string token, long id, string? path = null)
    {
        var url = $"/api/sessions/{id}/files" + (path is null ? string.Empty : "?path=" + Uri.EscapeDataString(path));
        var resp = await _fx.Client.SendAsync(Authed(HttpMethod.Get, url, token, null));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private async Task<HttpResponseMessage> DownloadAsync(string token, long id, string path, RangeHeaderValue range)
    {
        var req = Authed(HttpMethod.Get,
            $"/api/sessions/{id}/download?path=" + Uri.EscapeDataString(path), token, null);
        req.Headers.Range = range;
        return await _fx.Client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> DownloadWithRangeAsync(string token, long id, string path, string range)
    {
        var req = Authed(HttpMethod.Get,
            $"/api/sessions/{id}/download?path=" + Uri.EscapeDataString(path), token, null);
        req.Headers.TryAddWithoutValidation("Range", range);
        return await _fx.Client.SendAsync(req);
    }

    private async Task DeleteSessionAsync(string token, string csrf, long id)
    {
        var resp = await _fx.Client.SendAsync(Authed(HttpMethod.Delete, $"/api/sessions/{id}", token, csrf));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token, string? csrf)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("Cookie", $"litssh_token={token}; litssh_csrf={csrf}");
        if (csrf is not null)
            req.Headers.Add("X-CSRF-Token", csrf);
        return req;
    }

    /// <summary>创建目录链接（junction）；失败返回 false（测试跳过，同 PathGuardTests 先例）。</summary>
    private static bool TryCreateDirLink(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>共享工厂：访问日志换临时目录（断言 file_denied/file_download），其余与生产一致。</summary>
public sealed class FileFixture : IDisposable
{
    public string AccessLogDir { get; } =
        Path.Combine(Path.GetTempPath(), "litssh-filelog-" + Guid.NewGuid().ToString("N"));

    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }

    public FileFixture()
    {
        var logDir = AccessLogDir;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(services =>
            {
                services.RemoveAll<WebAccessLog>();
                services.AddSingleton(new WebAccessLog(logDir));
                services.RemoveAll<WebDownloadPolicy>();
                services.AddSingleton(new WebDownloadPolicy
                {
                    MaxBytes = 1024,
                    MaxConcurrentPerUser = 2,
                    Timeout = TimeSpan.FromSeconds(30)
                });
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
        var token = CookieFrom(resp, "litssh_token")!;
        return (token, csrf);
    }

    public async Task<(long Id, string Dir)> CreateSessionAsync(string token, string csrf)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/sessions")
        {
            Content = JsonContent.Create(new { title = "文件测试" })
        };
        req.Headers.Add("Cookie", $"litssh_token={token}; litssh_csrf={csrf}");
        req.Headers.Add("X-CSRF-Token", csrf);
        var resp = await Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("id").GetInt64(),
                body.RootElement.GetProperty("workspaceDir").GetString()!);
    }

    /// <summary>读全量访问日志（按文件名排序拼接，保证两次读取前缀稳定——日志只追加）。</summary>
    public string ReadAccessLog()
    {
        if (!Directory.Exists(AccessLogDir))
            return string.Empty;
        return string.Join('\n',
            Directory.EnumerateFiles(AccessLogDir, "web-access-*.log")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    /// <summary>自 <paramref name="before"/> 之后新增的日志段（同日志只追加，测试类内顺序执行）。</summary>
    public string LogAddedSince(string before)
    {
        var after = ReadAccessLog();
        return before.Length <= after.Length ? after[before.Length..] : after;
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        try { Directory.Delete(AccessLogDir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    private static string? CookieFrom(HttpResponseMessage resp, string name)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        foreach (var c in cookies)
        {
            if (c.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return c[(name.Length + 1)..].Split(';')[0];
        }
        return null;
    }
}
