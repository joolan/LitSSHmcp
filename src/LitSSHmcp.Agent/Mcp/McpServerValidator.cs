using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Agent;

/// <summary>MCP 连接校验结果（供设置页在"启用前"校验并记录）。</summary>
public sealed record McpValidationResult(bool Ok, int ToolCount, string? Error)
{
    public static McpValidationResult Fail(string error) => new(false, 0, error);
}

/// <summary>
/// 启用 MCP 服务器前的连接校验：完成一次 <c>initialize</c> + <c>tools/list</c>，
/// 成功返回工具数，失败返回可读错误（超时按 15 秒）。
/// </summary>
public static class McpServerValidator
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <param name="config">要校验的服务器配置（内置条目走 <paramref name="builtinServerPath"/> 解析的 exe）。</param>
    /// <param name="builtinServerPath">内置 MCP 服务器 exe 路径（<see cref="AgentConfig.McpServerPath"/>；空=自动探测）。</param>
    public static async Task<McpValidationResult> ValidateAsync(
        AgentMcpServerConfig config,
        string? builtinServerPath = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        McpToolHost host;
        try
        {
            if (config.Builtin)
            {
                var launch = AgentPaths.ResolveLaunch(builtinServerPath)
                    ?? throw new InvalidOperationException(
                        "未找到内置 MCP 服务器(LitSSHmcp.McpServer.exe)：请放到 App 的 mcp/ 目录或在「助手设置」指定路径。");
                host = McpToolHost.ForBuiltin(launch);
            }
            else
            {
                host = McpToolHost.Create(config);
            }
        }
        catch (Exception ex)
        {
            return McpValidationResult.Fail(Describe(ex));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            await host.ConnectAsync(cts.Token);
            var count = host.Tools.Count;
            await host.DisposeAsync();
            return new McpValidationResult(true, count, null);
        }
        catch (OperationCanceledException)
        {
            await host.DisposeAsync();
            return McpValidationResult.Fail($"连接超时（{Timeout.TotalSeconds:0} 秒无响应）");
        }
        catch (Exception ex)
        {
            await host.DisposeAsync();
            return McpValidationResult.Fail(Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var message = (ex.Message ?? ex.GetType().Name).Replace("\r", " ").Replace("\n", " ").Trim();
        return message.Length > 300 ? message[..300] + "…" : message;
    }
}
