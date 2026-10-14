using LitSSHmcp.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace LitSSHmcp.Agent;

/// <summary>
/// 持有一个 MCP 客户端（<see cref="McpClient"/>）：内置 LitSSH MCP 走本机 stdio 子进程，
/// 外部 MCP 按配置走 stdio 或 HTTP（SSE / Streamable HTTP）。工具即 <c>McpClientTool : AIFunction</c>。
/// </summary>
public sealed class McpToolHost : IAsyncDisposable
{
    private readonly Func<IClientTransport> _transportFactory;
    private McpClient? _client;

    private McpToolHost(Func<IClientTransport> transportFactory) => _transportFactory = transportFactory;

    /// <summary>服务器 initialize 返回的会话级行为约定（部分注入系统提示）。</summary>
    public string? ServerInstructions { get; private set; }

    public IReadOnlyList<McpClientTool> Tools { get; private set; } = Array.Empty<McpClientTool>();

    /// <summary>内置 LitSSH MCP（本机 stdio 子进程，路径由 <see cref="AgentPaths.ResolveLaunch"/> 解析）。</summary>
    public static McpToolHost ForBuiltin(McpServerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return new McpToolHost(() => new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "LitSSHmcp",
            Command = launch.FileName,
            Arguments = launch.Arguments.ToList(),
            WorkingDirectory = launch.WorkingDirectory
        }));
    }

    /// <summary>按配置创建外部 MCP 宿主：<c>transport=http</c> 走 HTTP，否则 stdio。</summary>
    public static McpToolHost Create(AgentMcpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return string.Equals(config.Transport, "http", StringComparison.OrdinalIgnoreCase)
            ? CreateHttp(config)
            : CreateStdio(config);
    }

    private static McpToolHost CreateStdio(AgentMcpServerConfig config)
    {
        var command = config.Command?.Trim() ?? string.Empty;
        if (command.Length == 0)
            throw new InvalidOperationException("stdio 服务器缺少 command（可执行文件）。");

        var arguments = (config.Args ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        var env = ParsePairs(config.Env, '=');

        return new McpToolHost(() => new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = string.IsNullOrWhiteSpace(config.Name) ? config.Id : config.Name,
            Command = command,
            Arguments = arguments,
            EnvironmentVariables = env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
            InheritEnvironmentVariables = true,
        }));
    }

    private static McpToolHost CreateHttp(AgentMcpServerConfig config)
    {
        var url = config.Url?.Trim() ?? string.Empty;
        if (url.Length == 0)
            throw new InvalidOperationException("http 服务器缺少 url。");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("http 服务器 url 必须是 http:// 或 https:// 绝对地址。");

        var headers = ParsePairs(config.Headers, ':');

        return new McpToolHost(() => new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = string.IsNullOrWhiteSpace(config.Name) ? config.Id : config.Name,
            Endpoint = uri,
            TransportMode = HttpTransportMode.AutoDetect,   // 先试 Streamable HTTP，回退 SSE
            AdditionalHeaders = headers.Count == 0 ? null : headers,
        }));
    }

    /// <summary>解析 <c>KEY=VALUE</c> / <c>Name: Value</c> 行为字典（跳过无分隔符的行）。</summary>
    public static Dictionary<string, string> ParsePairs(string[]? lines, char separator)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var i = line.IndexOf(separator);
            if (i <= 0)
                continue;
            var key = line[..i].Trim();
            var value = line[(i + 1)..].Trim();
            if (key.Length > 0)
                map[key] = value;
        }
        return map;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var transport = _transportFactory();
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
