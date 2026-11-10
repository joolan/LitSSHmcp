using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

public class WebExceptionMiddlewareTests
{
    private static async Task<(HttpContext Context, string LogDir)> InvokeAsync(
        PathString path, RequestDelegate next, CancellationToken requestAborted = default)
    {
        var logDir = Path.Combine(Path.GetTempPath(), "litssh-web-ex-" + Guid.NewGuid().ToString("N"));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.RequestAborted = requestAborted;
        context.Response.Body = new MemoryStream();

        var middleware = new WebExceptionMiddleware(next, NullLogger<WebExceptionMiddleware>.Instance);
        await middleware.InvokeAsync(context, new WebAccessLog(logDir));
        return (context, logDir);
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Api_exception_returns_json_500_and_writes_access_log()
    {
        var (context, logDir) = await InvokeAsync(
            "/api/boom",
            _ => throw new InvalidOperationException("boom"));

        try
        {
            Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
            Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
            Assert.Contains("internal_error", await ReadBodyAsync(context));

            var logFile = Assert.Single(Directory.GetFiles(logDir, "web-access-*.log"));
            var log = await File.ReadAllTextAsync(logFile);
            Assert.Contains("unhandled_exception", log);
            Assert.Contains("InvalidOperationException: boom", log);
        }
        finally
        {
            try { Directory.Delete(logDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Non_api_exception_returns_plain_500()
    {
        var (context, logDir) = await InvokeAsync(
            "/errors",
            _ => throw new InvalidOperationException("boom"));

        try
        {
            Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
            Assert.Equal("text/plain; charset=utf-8", context.Response.ContentType);
            Assert.Equal("Internal Server Error", await ReadBodyAsync(context));
        }
        finally
        {
            try { Directory.Delete(logDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Started_response_exception_is_logged_and_rethrown()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "litssh-web-ex-" + Guid.NewGuid().ToString("N"));
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/boom";

        var middleware = new WebExceptionMiddleware(
            _ => throw new InvalidOperationException("boom"),
            NullLogger<WebExceptionMiddleware>.Instance);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => middleware.InvokeAsync(context, new WebAccessLog(logDir)));

            var logFile = Assert.Single(Directory.GetFiles(logDir, "web-access-*.log"));
            Assert.Contains("unhandled_exception", await File.ReadAllTextAsync(logFile));
        }
        finally
        {
            try { Directory.Delete(logDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Client_disconnect_does_not_write_error_response_or_log()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (context, logDir) = await InvokeAsync(
            "/api/boom",
            _ => Task.FromException(new OperationCanceledException()),
            cts.Token);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.HasStarted);
        Assert.Empty(await ReadBodyAsync(context));
        Assert.False(Directory.Exists(logDir));
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
