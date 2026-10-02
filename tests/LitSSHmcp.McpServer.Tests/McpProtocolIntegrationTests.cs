using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>
/// MCP 协议集成测试：真正以 stdio 启动 MCP 服务器进程, 走 initialize → tools/list → tools/call 全链路。
/// 用临时数据目录(LITSSH_DATA_DIR)隔离, 不触碰用户真实配置。
/// </summary>
public class McpProtocolIntegrationTests
{
    [Fact]
    public async Task Server_exposes_tools_and_structured_output_over_stdio()
    {
        var exe = LocateServerExecutable();
        var dataDir = Path.Combine(Path.GetTempPath(), "litssh-mcp-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            using var client = new McpStdioClient(exe, dataDir);

            // 1) initialize
            var init = await client.RequestAsync("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "it", version = "1.0" }
            });
            var initResult = init.GetProperty("result");
            Assert.Equal("LitSSHmcp.McpServer", initResult.GetProperty("serverInfo").GetProperty("name").GetString());
            Assert.True(initResult.TryGetProperty("instructions", out var instructions));
            Assert.False(string.IsNullOrWhiteSpace(instructions.GetString()));

            await client.NotifyAsync("notifications/initialized");

            // 2) tools/list
            var list = await client.RequestAsync("tools/list", new { });
            var tools = list.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
            var names = tools.Select(t => t.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);

            Assert.Equal(49, tools.Count);
            foreach (var expected in new[] { "mysql_query", "postgres_diagnostics", "redis_read", "ssh_list_servers", "datasource_list", "topology_get_overview", "mcp_usage_guide", "mcp_self_check", "mcp_list_sessions", "docker_ps", "service_status", "log_tail", "log_find", "java_processes", "app_health_snapshot" })
                Assert.Contains(expected, names);

            foreach (var tool in tools)
            {
                Assert.True(tool.TryGetProperty("inputSchema", out var schema));
                Assert.Equal("object", schema.GetProperty("type").GetString());
            }

            // 结构化输出: SQL 工具有 outputSchema; 使用指南(text 文档)没有
            Assert.True(FindTool(tools, "mysql_query").TryGetProperty("outputSchema", out _));
            Assert.False(FindTool(tools, "mcp_usage_guide").TryGetProperty("outputSchema", out _));

            // 3) tools/call: mcp_self_check 应成功
            var selfCheck = await client.RequestAsync("tools/call", new { name = "mcp_self_check", arguments = new { } });
            var selfStructured = selfCheck.GetProperty("result").GetProperty("structuredContent");
            Assert.True(selfStructured.GetProperty("success").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(selfStructured.GetProperty("sessionId").GetString()));
            // 客户端信息来自 initialize 的 clientInfo（本测试客户端 name="it"），由会话过滤器在工具调用前回填
            Assert.Equal("it", selfStructured.GetProperty("clientName").GetString());

            // 会话列表应包含当前会话
            var sessions = await client.RequestAsync("tools/call", new { name = "mcp_list_sessions", arguments = new { } });
            var sessionsStructured = sessions.GetProperty("result").GetProperty("structuredContent");
            Assert.True(sessionsStructured.GetProperty("success").GetBoolean());
            Assert.True(sessionsStructured.GetProperty("count").GetInt32() >= 1);

            // 4) tools/call: mysql_query 非法数据源 → 结构化错误
            var badQuery = await client.RequestAsync("tools/call", new
            {
                name = "mysql_query",
                arguments = new { datasourceId = "nope", sql = "select 1" }
            });
            var badResult = badQuery.GetProperty("result");
            Assert.Equal("datasource_not_found", badResult.GetProperty("structuredContent").GetProperty("status").GetString());
            // 兼容层: 同时应带 text(JSON) 内容
            Assert.Equal("text", badResult.GetProperty("content")[0].GetProperty("type").GetString());

            // 5) tools/call: 使用指南(text) 包含工具清单
            var guide = await client.RequestAsync("tools/call", new { name = "mcp_usage_guide", arguments = new { } });
            var guideText = guide.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Contains("mysql_query", guideText);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* 忽略清理失败 */ }
        }
    }

    [Fact]
    public async Task Global_switch_disabled_rejects_all_tool_calls()
    {
        var exe = LocateServerExecutable();
        var dataDir = Path.Combine(Path.GetTempPath(), "litssh-mcp-off-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        // 预置配置：全局 MCP 开关关闭
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "config.json"),
            "{\"schemaVersion\":1,\"security\":{\"enabled\":false}}",
            Encoding.UTF8);

        try
        {
            using var client = new McpStdioClient(exe, dataDir);

            await client.RequestAsync("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "it", version = "1.0" }
            });
            await client.NotifyAsync("notifications/initialized");

            // 工具仍会列出
            var list = await client.RequestAsync("tools/list", new { });
            Assert.Equal(49, list.GetProperty("result").GetProperty("tools").GetArrayLength());

            // 但任何调用都被拒绝
            var call = await client.RequestAsync("tools/call", new { name = "mcp_self_check", arguments = new { } });
            var result = call.GetProperty("result");
            Assert.True(result.GetProperty("isError").GetBoolean());
            var text = result.GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Contains("禁用", text);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* 忽略清理失败 */ }
        }
    }

    private static JsonElement FindTool(List<JsonElement> tools, string name) =>
        tools.First(t => t.GetProperty("name").GetString() == name);

    private static string LocateServerExecutable()
    {
        var root = FindRepoRoot();
        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(root, "src", "LitSSHmcp.McpServer", "bin", config, "net8.0-windows", "LitSSHmcp.McpServer.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("未找到 LitSSHmcp.McpServer.exe，请先构建 src/LitSSHmcp.McpServer。");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LitSSHmcp.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("未找到仓库根目录(含 LitSSHmcp.slnx)");
    }

    /// <summary>极简 MCP stdio 客户端：换行分隔 JSON-RPC，按 id 匹配响应。</summary>
    private sealed class McpStdioClient : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _stdin;
        private int _nextId;

        public McpStdioClient(string exe, string dataDir)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["LITSSH_DATA_DIR"] = dataDir;

            _process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 MCP 服务器进程");
            _stdin = _process.StandardInput;

            // 排空 stderr，避免缓冲区写满导致服务器阻塞
            _ = Task.Run(() => _process.StandardError.ReadToEnd());
        }

        public Task<JsonElement> RequestAsync(string method, object? @params)
        {
            var id = ++_nextId;
            var message = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params
            });
            return SendAndWaitAsync(message, id);
        }

        public async Task NotifyAsync(string method)
        {
            var message = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method
            });
            await _stdin.WriteLineAsync(message);
            await _stdin.FlushAsync();
        }

        private async Task<JsonElement> SendAndWaitAsync(string message, int id)
        {
            await _stdin.WriteLineAsync(message);
            await _stdin.FlushAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(timeout.Token);
                if (line is null)
                    throw new InvalidOperationException("MCP 服务器关闭了 stdout");

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement) &&
                    idElement.ValueKind == JsonValueKind.Number &&
                    idElement.GetInt32() == id)
                {
                    return root.Clone();
                }
            }
        }

        public void Dispose()
        {
            try { _stdin.Close(); } catch { /* ignore */ }
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            _process.Dispose();
        }
    }
}
