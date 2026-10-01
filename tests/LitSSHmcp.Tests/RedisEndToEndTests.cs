using System.Net;
using System.Net.Sockets;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>
/// 用内存假 RESP 服务器做端到端验证：握手(AUTH/SELECT)、只读查询、安全策略拦截、诊断采集。
/// </summary>
public class RedisEndToEndTests
{
    [Fact]
    public async Task Provider_sends_auth_and_select_then_driver_reads()
    {
        using var server = new FakeRedisServer();
        var provider = CreateProvider();
        var ds = CreateDataSource(server.Port);

        using var session = await provider.OpenAsync(ds);
        var pong = await session.Client.ExecuteAsync(new[] { "PING" });
        Assert.Equal("PONG", pong.Text);

        Assert.Contains(server.Received, c => c.SequenceEqual(new[] { "AUTH", "s3cret" }));
        Assert.Contains(server.Received, c => c.SequenceEqual(new[] { "SELECT", "2" }));

        var driver = new RedisDriver(provider);
        var query = await driver.QueryAsync(ds, "GET foo", 100);
        Assert.True(query.Success, query.Error);
        Assert.Equal(new[] { "value" }, query.Columns);
        Assert.Equal(1, query.RowCount);
        Assert.Equal("hello", query.Rows[0]["value"]);
    }

    [Fact]
    public async Task Driver_blocks_writes_on_read_path_and_dangerous_commands()
    {
        using var server = new FakeRedisServer();
        var provider = CreateProvider();
        var ds = CreateDataSource(server.Port);
        var driver = new RedisDriver(provider);

        var writeOnReadPath = await driver.QueryAsync(ds, "SET k v", 100);
        Assert.False(writeOnReadPath.Success);
        Assert.Contains("非只读命令", writeOnReadPath.Error);

        var dangerous = await driver.ExecuteAsync(ds, "FLUSHALL");
        Assert.False(dangerous.Success);
        Assert.Contains("禁止执行", dangerous.Error);

        await Task.Delay(100);
        Assert.DoesNotContain(server.Received, c => c[0].Equals("FLUSHALL", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(server.Received, c => c[0].Equals("SET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Test_and_diagnostics_collect_from_info_and_aux_commands()
    {
        using var server = new FakeRedisServer();
        var provider = CreateProvider();
        var ds = CreateDataSource(server.Port);
        var driver = new RedisDriver(provider);

        var test = await driver.TestAsync(ds);
        Assert.True(test.Success, test.Error);
        Assert.Equal("redis 7.2.4", test.Version);
        Assert.Equal("Direct", test.AccessMode);

        var diagnostics = await driver.DiagnoseAsync(ds);
        Assert.True(diagnostics.Success, diagnostics.Error);
        Assert.Contains("Redis 7.2.4", diagnostics.Summary);
        Assert.Contains("role=master", diagnostics.Summary);
        Assert.Equal(42L, diagnostics.Data["totalKeys"]);

        var explain = await driver.ExplainAsync(ds, "SELECT 1");
        Assert.False(explain.Success);
        Assert.Contains("redis_diagnostics", explain.Error);
    }

    private static DataSourceConfig CreateDataSource(int port) => new()
    {
        Id = "redis-e2e",
        Name = "假缓存",
        Type = "redis",
        Host = "127.0.0.1",
        Port = port,
        Password = "s3cret",
        DefaultDatabase = "2",
        AccessMode = AccessMode.Direct
    };

    private static RedisConnectionProvider CreateProvider() =>
        new(new StubConfigService(), new StubKnownHosts(), new FakeSecurityOptions(), new StubLimiter());

    private sealed class StubConfigService : IConfigService
    {
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(new AppConfig());
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }

    private sealed class StubKnownHosts : ISshKnownHostsStore
    {
        public SshKnownHost? Find(string host, int port) => null;
        public IReadOnlyList<SshKnownHost> GetAll() => Array.Empty<SshKnownHost>();
        public void Save(SshKnownHost entry) { }
        public bool Remove(string host, int port) => false;
    }

    private sealed class StubLimiter : ITargetLimiter
    {
        public bool TryAcquire(string targetKey, out IDisposable? lease, out string? reason)
        {
            lease = null;
            reason = null;
            return true;
        }
    }

    private sealed class FakeRedisServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string[]> _received = new();
        private readonly object _gate = new();

        public FakeRedisServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public IReadOnlyList<string[]> Received
        {
            get { lock (_gate) return _received.ToList(); }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var c = client;
            var stream = c.GetStream();
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var args = await ReadCommandAsync(stream, _cts.Token);
                    if (args == null || args.Length == 0) return;

                    lock (_gate) _received.Add(args);
                    var response = Respond(args);
                    await stream.WriteAsync(response, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }

        private static async Task<string[]?> ReadCommandAsync(NetworkStream stream, CancellationToken ct)
        {
            var header = await ReadLineAsync(stream, ct);
            if (header == null || header.Length < 2 || header[0] != '*') return null;
            if (!int.TryParse(header[1..], out var count) || count < 0) return null;

            var args = new string[count];
            for (var i = 0; i < count; i++)
            {
                var lenLine = await ReadLineAsync(stream, ct);
                if (lenLine == null || lenLine.Length < 2 || lenLine[0] != '$') return null;
                if (!int.TryParse(lenLine[1..], out var length) || length < 0) return null;

                var buffer = new byte[length + 2];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
                    if (read == 0) return null;
                    offset += read;
                }

                args[i] = Encoding.UTF8.GetString(buffer, 0, length);
            }

            return args;
        }

        private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(one.AsMemory(), ct);
                if (read == 0) return null;
                if (one[0] == (byte)'\n')
                {
                    if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }
                bytes.Add(one[0]);
            }
        }

        private static byte[] Respond(string[] args)
        {
            var name = args[0].ToUpperInvariant();
            var body = name switch
            {
                "AUTH" or "SELECT" => "+OK\r\n",
                "PING" => "+PONG\r\n",
                "INFO" => Bulk(
                    "# Server\r\n" +
                    "redis_version:7.2.4\r\n" +
                    "redis_mode:standalone\r\n" +
                    "os:Linux\r\n" +
                    "tcp_port:6379\r\n" +
                    "# Replication\r\n" +
                    "role:master\r\n" +
                    "connected_slaves:0\r\n"),
                "DBSIZE" => ":42\r\n",
                "GET" => Bulk("hello"),
                "CLIENT" => Bulk("id=1 addr=127.0.0.1:50000 name= cmd=get age=10 idle=0 db=0\r\n"),
                "SLOWLOG" => "*0\r\n",
                "CONFIG" => "*0\r\n",
                _ => $"-ERR unknown command '{name}'\r\n"
            };
            return Encoding.UTF8.GetBytes(body);
        }

        private static string Bulk(string text)
        {
            var length = Encoding.UTF8.GetByteCount(text);
            return $"${length}\r\n{text}\r\n";
        }
    }
}
