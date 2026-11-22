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
