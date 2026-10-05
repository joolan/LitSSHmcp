using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace LitSSHmcp.Agent;

/// <summary>以 stdio 启动本机 MCP 服务器并持有 <see cref="McpClient"/>（工具即 <c>McpClientTool : AIFunction</c>）。</summary>
public sealed class McpToolHost : IAsyncDisposable
{
    private readonly McpServerLaunch _launch;
    private McpClient? _client;

    public McpToolHost(McpServerLaunch launch) => _launch = launch;

    /// <summary>服务器 initialize 返回的会话级行为约定（部分注入系统提示）。</summary>
    public string? ServerInstructions { get; private set; }

    public IReadOnlyList<McpClientTool> Tools { get; private set; } = Array.Empty<McpClientTool>();

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "LitSSHmcp",
            Command = _launch.FileName,
            Arguments = _launch.Arguments.ToList(),
            WorkingDirectory = _launch.WorkingDirectory
        });

        _client = await McpClient.CreateAsync(transport, null, null, ct);
        ServerInstructions = _client.ServerInstructions;
        Tools = (await _client.ListToolsAsync((RequestOptions?)null, ct)).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            try { await _client.DisposeAsync(); } catch { /* 关停子进程失败忽略 */ }
            _client = null;
        }
    }
}
