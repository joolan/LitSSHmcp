using System.Net;
using System.Net.Sockets;
using System.Text;
using LitSSHmcp.Core.Services.Proxy;
using Xunit;

namespace LitSSHmcp.Tests.Proxy;

public class ProxyIntegrationTests
{
    [Fact]
    public async Task Http_proxy_forwards_absolute_uri()
    {
        await using var origin = TestOrigin.Start();
        await using var host = await StartProxyAsync(origin.Port, blockPrivate: false);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.HttpEndpoint));
        var stream = client.GetStream();
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("ops:secret"));

        var request = $"GET http://127.0.0.1:{origin.Port}/hello HTTP/1.1\r\n" +
                      $"Host: 127.0.0.1:{origin.Port}\r\n" +
                      $"Proxy-Authorization: Basic {auth}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        var response = await ReadAllAsync(stream);
        Assert.Contains("200 OK", response);
        Assert.Contains("hello-from-origin", response);
    }

    [Fact]
    public async Task Http_connect_tunnels_to_origin()
    {
        await using var origin = TestOrigin.Start();
        await using var host = await StartProxyAsync(origin.Port, blockPrivate: false);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.HttpEndpoint));
        var stream = client.GetStream();
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("ops:secret"));

        var connect = $"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\n" +
                      $"Host: 127.0.0.1:{origin.Port}\r\n" +
                      $"Proxy-Authorization: Basic {auth}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(connect));
        var connectResponse = await ReadHeaderAsync(stream);
        Assert.Contains("200 Connection Established", connectResponse);

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /hello HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nConnection: close\r\n\r\n"));
        var response = await ReadAllAsync(stream);
        Assert.Contains("200 OK", response);
        Assert.Contains("hello-from-origin", response);
    }

    [Fact]
    public async Task Http_connect_forwards_bytes_pipelined_with_headers()
    {
        await using var origin = TestOrigin.Start();
        await using var host = await StartProxyAsync(origin.Port, blockPrivate: false);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.HttpEndpoint));
        var stream = client.GetStream();
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("ops:secret"));

        var payload = $"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\n" +
                      $"Host: 127.0.0.1:{origin.Port}\r\n" +
                      $"Proxy-Authorization: Basic {auth}\r\n\r\n" +
                      $"GET /hello HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(payload));

        var connectResponse = await ReadHeaderAsync(stream);
        Assert.Contains("200 Connection Established", connectResponse);
        var response = await ReadAllAsync(stream);
        Assert.Contains("200 OK", response);
        Assert.Contains("hello-from-origin", response);
    }

    [Fact]
    public async Task Socks5_authenticates_and_tunnels()
    {
        await using var origin = TestOrigin.Start();
        await using var host = await StartProxyAsync(origin.Port, blockPrivate: false);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.Socks5Endpoint));
        var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 5, 2, 0, 2 });
        var method = new byte[2];
        await ReadExactAsync(stream, method);
        Assert.Equal(5, method[0]);
        Assert.Equal(2, method[1]);

        await WriteSocksAuthAsync(stream, "ops", "secret");
        var authResult = new byte[2];
        await ReadExactAsync(stream, authResult);
        Assert.Equal(0x01, authResult[0]);
        Assert.Equal(0x00, authResult[1]);

        await WriteSocksConnectAsync(stream, "127.0.0.1", origin.Port);
        var reply = new byte[10];
        await ReadExactAsync(stream, reply);
        Assert.Equal(5, reply[0]);
        Assert.Equal(0x00, reply[1]);

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /hello HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nConnection: close\r\n\r\n"));
        var response = await ReadAllAsync(stream);
        Assert.Contains("200 OK", response);
        Assert.Contains("hello-from-origin", response);
    }

    [Fact]
    public async Task HttpClient_via_webproxy_works()
    {
        await using var origin = TestOrigin.Start();
        await using var host = await StartProxyAsync(origin.Port, blockPrivate: false);

        var proxy = new WebProxy($"http://127.0.0.1:{PortOf(host.Status.HttpEndpoint)}")
        {
            BypassProxyOnLocal = false,
            Credentials = new NetworkCredential("ops", "secret")
        };
        using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/hello");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello-from-origin", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Http_requires_authentication_unless_anonymous()
    {
        await using var origin = TestOrigin.Start();
        await using var host = await StartProxyAsync(origin.Port, blockPrivate: false);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.HttpEndpoint));
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"GET http://127.0.0.1:{origin.Port}/hello HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n"));
            var response = await ReadAllAsync(stream);
            Assert.Contains("407 Proxy Authentication Required", response);
        }
    }

    [Fact]
    public async Task Anonymous_mode_allows_http_and_socks_without_credentials()
    {
        await using var origin = TestOrigin.Start();
        await using var host = await StartProxyAsync(origin.Port, blockPrivate: false, anonymous: true);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.HttpEndpoint));
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"GET http://127.0.0.1:{origin.Port}/hello HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n"));
            var response = await ReadAllAsync(stream);
            Assert.Contains("200 OK", response);
            Assert.Contains("hello-from-origin", response);
        }

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.Socks5Endpoint));
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 });
            var method = new byte[2];
            await ReadExactAsync(stream, method);
            Assert.Equal(5, method[0]);
            Assert.Equal(0, method[1]);
        }
    }

    [Fact]
    public async Task Socks5_wrong_password_is_rejected()
    {
        await using var origin = TestOrigin.Start();
        await using var host = await StartProxyAsync(origin.Port, blockPrivate: false);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.Socks5Endpoint));
        var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 5, 1, 2 });
        var method = new byte[2];
        await ReadExactAsync(stream, method);
        Assert.Equal(2, method[1]);

        await WriteSocksAuthAsync(stream, "ops", "wrong");
        var authResult = new byte[2];
        await ReadExactAsync(stream, authResult);
        Assert.Equal(0x01, authResult[0]);
        Assert.Equal(0x01, authResult[1]);
    }

    [Fact]
    public async Task Private_destination_is_rejected_by_policy()
    {
        await using var origin = TestOrigin.Start();
        await using var host = await StartProxyAsync(origin.Port, blockPrivate: true);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.HttpEndpoint));
            var stream = client.GetStream();
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("ops:secret"));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"GET http://127.0.0.1:{origin.Port}/hello HTTP/1.1\r\n" +
                $"Host: 127.0.0.1:{origin.Port}\r\nProxy-Authorization: Basic {auth}\r\n\r\n"));
            var response = await ReadAllAsync(stream);
            Assert.Contains("403 Forbidden", response);
        }

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.Socks5Endpoint));
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 2 });
            await ReadExactAsync(stream, new byte[2]);
            await WriteSocksAuthAsync(stream, "ops", "secret");
            await ReadExactAsync(stream, new byte[2]);
            await WriteSocksConnectAsync(stream, "127.0.0.1", origin.Port);
            var reply = new byte[10];
            await ReadExactAsync(stream, reply);
            Assert.Equal(0x02, reply[1]);
        }
    }

    [Fact]
    public async Task Hot_reload_updates_accounts_and_client_whitelist()
    {
        await using var origin = TestOrigin.Start();
        var options = BuildOptions(origin.Port, blockPrivate: false);
        var oldAccount = CreateAccount("old", "old-secret");
        await using var host = await StartWithOptionsAsync(options, oldAccount);

        var first = await HttpGetViaProxyAsync(host, "old", "old-secret", origin.Port);
        Assert.Contains("200 OK", first);

        var newAccount = CreateAccount("new", "new-secret");
        var reload = options.Clone();
        host.UpdateRuntime(reload, new[] { newAccount });

        var oldRejected = await HttpGetViaProxyAsync(host, "old", "old-secret", origin.Port);
        Assert.Contains("407 Proxy Authentication Required", oldRejected);
        var newAllowed = await HttpGetViaProxyAsync(host, "new", "new-secret", origin.Port);
        Assert.Contains("200 OK", newAllowed);

        var whitelisted = reload.Clone();
        whitelisted.AllowedClientCidrs = new List<string> { "10.0.0.0/8" };
        host.UpdateRuntime(whitelisted, new[] { newAccount });
        var blocked = await HttpGetViaProxyAsync(host, "new", "new-secret", origin.Port);
        Assert.Contains("403 Forbidden", blocked);

        var portChange = whitelisted.Clone();
        portChange.HttpPort = 18888;
        var before = host.Status.HttpEndpoint;
        var restart = host.UpdateRuntime(portChange, new[] { newAccount });
        Assert.True(restart.RestartRequired);
        Assert.Equal(before, host.Status.HttpEndpoint);
    }

    [Fact]
    public async Task Active_connection_can_be_disconnected()
    {
        await using var origin = TestOrigin.Start();
        await using var host = await StartProxyAsync(origin.Port, blockPrivate: false);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.HttpEndpoint));
        var stream = client.GetStream();
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("ops:secret"));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{origin.Port}\r\nProxy-Authorization: Basic {auth}\r\n\r\n"));
        Assert.Contains("200 Connection Established", await ReadHeaderAsync(stream));

        await WaitForAsync(() => host.ActiveConnections.Count == 1);
        var connection = Assert.Single(host.ActiveConnections);
        Assert.Contains(origin.Port.ToString(), connection.Target);
        Assert.Equal("隧道", connection.State);

        Assert.True(host.Disconnect(connection.Id));
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var n = await stream.ReadAsync(new byte[1], readCts.Token);
            Assert.Equal(0, n);
        }
        catch (IOException)
        {
            // 断开连接导致读取失败同样符合预期
        }
        await WaitForAsync(() => host.ActiveConnections.Count == 0);
    }

    [Fact]
    public async Task Auth_failure_limit_blocks_http_client()
    {
        await using var origin = TestOrigin.Start();
        var options = BuildOptions(origin.Port, blockPrivate: false);
        options.AuthFailureLimit = 2;
        var account = CreateAccount("ops", "secret");
        await using var host = await StartWithOptionsAsync(options, account);

        var first = await HttpGetViaProxyAsync(host, "ops", "wrong-1", origin.Port);
        Assert.Contains("407 Proxy Authentication Required", first);
        var second = await HttpGetViaProxyAsync(host, "ops", "wrong-2", origin.Port);
        Assert.Contains("429 Too Many Requests", second);
        var blocked = await HttpGetViaProxyAsync(host, "ops", "secret", origin.Port);
        Assert.Contains("429 Too Many Requests", blocked);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("socks5")]
    public async Task Upstream_proxy_chain_works(string upstreamType)
    {
        await using var origin = TestOrigin.Start();
        var upstreamOptions = BuildOptions(origin.Port, blockPrivate: false);
        upstreamOptions.AllowAnonymous = true;
        await using var upstream = await StartWithOptionsAsync(upstreamOptions);

        var outerOptions = BuildOptions(origin.Port, blockPrivate: false);
        outerOptions.AllowAnonymous = true;
        outerOptions.UpstreamProxyEnabled = true;
        outerOptions.UpstreamProxyType = upstreamType;
        outerOptions.UpstreamProxyHost = "127.0.0.1";
        outerOptions.UpstreamProxyPort = upstreamType == "http"
            ? PortOf(upstream.Status.HttpEndpoint)
            : PortOf(upstream.Status.Socks5Endpoint);
        await using var outer = await StartWithOptionsAsync(outerOptions);

        var response = await HttpGetViaProxyAsync(outer, null, null, origin.Port);
        Assert.Contains("200 OK", response);
        Assert.Contains("hello-from-origin", response);
    }

    private static async Task<string> HttpGetViaProxyAsync(ProxyServerHost host,
        string? username, string? password, int targetPort)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, PortOf(host.Status.HttpEndpoint));
        var stream = client.GetStream();
        var auth = string.IsNullOrEmpty(username)
            ? string.Empty
            : "Proxy-Authorization: Basic " +
              Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)) + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET http://127.0.0.1:{targetPort}/hello HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{targetPort}\r\n{auth}\r\n"));
        return await ReadAllAsync(stream);
    }

    private static async Task<ProxyServerHost> StartWithOptionsAsync(ProxyOptions options,
        params ProxyAccount[] accounts)
    {
        var host = new ProxyServerHost();
        await host.StartAsync(options, accounts);
        return host;
    }

    private static ProxyAccount CreateAccount(string username, string password)
    {
        var account = new ProxyAccount { Username = username, Enabled = true };
        account.SetPassword(password, 10_000);
        return account;
    }

    private static ProxyOptions BuildOptions(int allowedPort, bool blockPrivate) => new()
    {
        BindAddress = "127.0.0.1",
        HttpPort = 0,
        Socks5Port = 0,
        AllowedPorts = new List<int> { allowedPort },
        BlockPrivateDestinations = blockPrivate,
        TemporaryEnableMinutes = 0,
        ConnectTimeoutSeconds = 5,
        HeaderTimeoutSeconds = 5,
        IdleTimeoutSeconds = 10,
        MaxTotalConnections = 16,
        MaxConnectionsPerClient = 8
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 60; i++)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail("等待条件超时。");
    }

    private static async Task<ProxyServerHost> StartProxyAsync(int allowedPort,
        bool blockPrivate, bool anonymous = false)
    {
        var account = new ProxyAccount { Username = "ops", Enabled = true };
        account.SetPassword("secret", 10_000);
        var options = new ProxyOptions
        {
            BindAddress = "127.0.0.1",
            HttpPort = 0,
            Socks5Port = 0,
            AllowAnonymous = anonymous,
            AllowedPorts = new List<int> { allowedPort },
            BlockPrivateDestinations = blockPrivate,
            TemporaryEnableMinutes = 0,
            ConnectTimeoutSeconds = 5,
            HeaderTimeoutSeconds = 5,
            IdleTimeoutSeconds = 10,
            MaxTotalConnections = 16,
            MaxConnectionsPerClient = 8
        };
        var host = new ProxyServerHost();
        await host.StartAsync(options, new[] { account });
        return host;
    }

    private static int PortOf(string? endpoint)
    {
        Assert.False(string.IsNullOrWhiteSpace(endpoint));
        var text = endpoint!;
        return int.Parse(text[(text.LastIndexOf(':') + 1)..]);
    }

    private static async Task WriteSocksAuthAsync(Stream stream, string username, string password)
    {
        var user = Encoding.UTF8.GetBytes(username);
        var pass = Encoding.UTF8.GetBytes(password);
        var packet = new byte[3 + user.Length + pass.Length];
        packet[0] = 0x01;
        packet[1] = (byte)user.Length;
        Buffer.BlockCopy(user, 0, packet, 2, user.Length);
        packet[2 + user.Length] = (byte)pass.Length;
        Buffer.BlockCopy(pass, 0, packet, 3 + user.Length, pass.Length);
        await stream.WriteAsync(packet);
    }

    private static async Task WriteSocksConnectAsync(Stream stream, string ipv4, int port)
    {
        var address = IPAddress.Parse(ipv4).GetAddressBytes();
        var packet = new byte[10];
        packet[0] = 0x05;
        packet[1] = 0x01;
        packet[2] = 0x00;
        packet[3] = 0x01;
        Buffer.BlockCopy(address, 0, packet, 4, 4);
        packet[8] = (byte)(port >> 8);
        packet[9] = (byte)(port & 0xFF);
        await stream.WriteAsync(packet);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token);
            if (n == 0)
                throw new EndOfStreamException();
            read += n;
        }
    }

    private static async Task<string> ReadHeaderAsync(Stream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var data = new List<byte>();
        var buffer = new byte[1024];
        while (data.Count < 16 * 1024)
        {
            var n = await stream.ReadAsync(buffer, cts.Token);
            if (n == 0)
                break;
            for (var i = 0; i < n; i++)
                data.Add(buffer[i]);
            if (data.Count >= 4 &&
                data[^4] == 13 && data[^3] == 10 && data[^2] == 13 && data[^1] == 10)
                break;
        }
        return Encoding.ASCII.GetString(data.ToArray());
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var collected = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, cts.Token);
                if (n == 0)
                    break;
                collected.Write(buffer, 0, n);
            }
        }
        catch (OperationCanceledException)
        {
            // 返回已读取部分便于断言
        }
        return Encoding.ASCII.GetString(collected.ToArray());
    }

    private sealed class TestOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private TestOrigin(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public static TestOrigin Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new TestOrigin(listener);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _loop; } catch { /* ignore */ }
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                _ = ServeAsync(client);
            }
        }

        private static async Task ServeAsync(TcpClient client)
        {
            using var owned = client;
            try
            {
                var stream = client.GetStream();
                var header = await ReadHeaderAsync(stream);
                if (string.IsNullOrWhiteSpace(header))
                    return;
                const string body = "hello-from-origin";
                var response = "HTTP/1.1 200 OK\r\n" +
                               "Content-Type: text/plain\r\n" +
                               $"Content-Length: {body.Length}\r\n" +
                               "Connection: close\r\n\r\n" + body;
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
            catch
            {
                // 测试服务器忽略单个连接错误
            }
        }
    }
}
