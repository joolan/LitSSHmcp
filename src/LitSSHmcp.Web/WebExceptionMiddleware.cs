using Microsoft.AspNetCore.Http;

namespace LitSSHmcp.Web;

/// <summary>Web 未处理异常：记录访问日志并返回稳定的 500，不向客户端泄露堆栈。</summary>
public sealed class WebExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<WebExceptionMiddleware> _logger;

    public WebExceptionMiddleware(RequestDelegate next, ILogger<WebExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, WebAccessLog accessLog)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端断开：不记错误
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled Web request exception: {Method} {Path}",
                context.Request.Method, context.Request.Path);
            accessLog.Write("unhandled_exception",
                context.GetWebPrincipal()?.Username,
                context.Connection.RemoteIpAddress?.ToString(),
                $"{context.Request.Method} {context.Request.Path}: {ex.GetType().Name}: {ex.Message}");

            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(new { error = "internal_error" });
            }
            else
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("Internal Server Error");
            }
        }
    }
}
