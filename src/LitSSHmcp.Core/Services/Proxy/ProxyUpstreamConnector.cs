using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>目标连接：按配置直连或经上游 HTTP / SOCKS5 代理链。</summary>
internal static class ProxyTargetConnector
{
    public static Task<ProxyConnectResult> ConnectAsync(ProxyOptions options, string host,
        int port, IPAddress[] addresses, CancellationToken ct)
    {
        if (options.UpstreamProxyEnabled)
            return ProxyUpstreamConnector.ConnectAsync(options, host, port, addresses, ct);
        return ProxyTunnel.ConnectAsync(addresses, port, options.ConnectTimeoutSeconds, ct);
    }
}

/// <summary>上游代理握手。上游地址由用户显式配置，因此不再套用目标私有地址限制。</summary>
internal static class ProxyUpstreamConnector
{
    public static async Task<ProxyConnectResult> ConnectAsync(ProxyOptions options,
        string targetHost, int targetPort, IPAddress[] addresses, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.UpstreamProxyHost) ||
            options.UpstreamProxyPort is < 1 or > 65535)
            return new ProxyConnectResult(null, false, false, "上游代理地址或端口未配置。");

        IPAddress[] upstreamAddresses;
        if (IPAddress.TryParse(options.UpstreamProxyHost, out var literal))
            upstreamAddresses = new[] { ProxyPolicy.NormalizeAddress(literal) };
        else
        {
            try
            {
                upstreamAddresses = (await Dns.GetHostAddressesAsync(options.UpstreamProxyHost, ct))
                    .Select(ProxyPolicy.NormalizeAddress).Distinct().ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ProxyConnectResult(null, false, false,
                    "上游代理域名解析失败：" + ex.Message);
            }
        }

        var connected = await ProxyTunnel.ConnectAsync(upstreamAddresses,
            options.UpstreamProxyPort, options.ConnectTimeoutSeconds, ct);
        if (!connected.Success)
            return new ProxyConnectResult(null, connected.TimedOut, connected.Refused,
                "连接上游代理失败：" + (connected.Error ?? "未知错误"));

        var client = connected.Client!;
        try
        {
            var stream = client.GetStream();
            var targetForUpstream = addresses.FirstOrDefault(address =>
                    address.AddressFamily == AddressFamily.InterNetwork)?.ToString()
                ?? addresses.FirstOrDefault()?.ToString()
                ?? targetHost;
            if (string.Equals(options.UpstreamProxyType, "socks5", StringComparison.OrdinalIgnoreCase))
                await Socks5HandshakeAsync(stream, options, targetForUpstream, targetPort, ct);
            else
                await HttpConnectHandshakeAsync(stream, options, targetForUpstream, targetPort, ct);
            return new ProxyConnectResult(client, false, false, null);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            client.Dispose();
            return new ProxyConnectResult(null, false, false, "上游代理握手失败：" + ex.Message);
        }
    }

    private static async Task HttpConnectHandshakeAsync(Stream stream, ProxyOptions options,
        string targetHost, int targetPort, CancellationToken ct)
    {
        var authority = FormatAuthority(targetHost, targetPort);
        var request = new StringBuilder();
        request.Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n");
        request.Append("Host: ").Append(authority).Append("\r\n");
        request.Append("Proxy-Connection: keep-alive\r\n");
        if (!string.IsNullOrWhiteSpace(options.UpstreamProxyUsername))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                options.UpstreamProxyUsername + ":" + options.UpstreamProxyPassword));
            request.Append("Proxy-Authorization: Basic ").Append(credentials).Append("\r\n");
        }
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct);

        var header = await ReadHttpHeaderAsync(stream, 16 * 1024, ct);
        var statusLine = Encoding.ASCII.GetString(header)
            .Split("\r\n", StringSplitOptions.None)[0];
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var status) ||
            status is < 200 or > 299)
            throw new IOException("上游 HTTP 代理返回非 2xx：" + statusLine);
    }

    private static async Task Socks5HandshakeAsync(Stream stream, ProxyOptions options,
        string targetHost, int targetPort, CancellationToken ct)
    {
        var hasCredentials = !string.IsNullOrWhiteSpace(options.UpstreamProxyUsername);
        await stream.WriteAsync(hasCredentials ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct);
        var method = new byte[2];
        await ReadExactAsync(stream, method, ct);
        if (method[0] != 5)
            throw new IOException("上游 SOCKS5 版本不匹配。");

        if (method[1] == 0x02)
        {
            var user = Encoding.UTF8.GetBytes(options.UpstreamProxyUsername);
            var pass = Encoding.UTF8.GetBytes(options.UpstreamProxyPassword);
            if (user.Length is 0 or > 255 || pass.Length > 255)
                throw new IOException("上游 SOCKS5 用户名或密码过长。");
            var packet = new byte[3 + user.Length + pass.Length];
            packet[0] = 0x01;
            packet[1] = (byte)user.Length;
            Buffer.BlockCopy(user, 0, packet, 2, user.Length);
            packet[2 + user.Length] = (byte)pass.Length;
            Buffer.BlockCopy(pass, 0, packet, 3 + user.Length, pass.Length);
            await stream.WriteAsync(packet, ct);
            var authResult = new byte[2];
            await ReadExactAsync(stream, authResult, ct);
            if (authResult[0] != 0x01 || authResult[1] != 0x00)
                throw new IOException("上游 SOCKS5 认证失败。");
        }
        else if (method[1] != 0x00)
        {
            throw new IOException("上游 SOCKS5 不接受当前认证方式。");
        }

        byte[] addressBytes;
        byte addressType;
        if (IPAddress.TryParse(targetHost, out var address))
        {
            address = ProxyPolicy.NormalizeAddress(address);
            addressBytes = address.GetAddressBytes();
            addressType = address.AddressFamily == AddressFamily.InterNetworkV6
                ? (byte)0x04 : (byte)0x01;
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(targetHost);
            if (bytes.Length is 0 or > 255)
                throw new IOException("目标域名过长。");
            addressBytes = new byte[1 + bytes.Length];
            addressBytes[0] = (byte)bytes.Length;
            Buffer.BlockCopy(bytes, 0, addressBytes, 1, bytes.Length);
            addressType = 0x03;
        }

        var request = new byte[4 + addressBytes.Length + 2];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = addressType;
        Buffer.BlockCopy(addressBytes, 0, request, 4, addressBytes.Length);
        request[^2] = (byte)(targetPort >> 8);
        request[^1] = (byte)(targetPort & 0xFF);
        await stream.WriteAsync(request, ct);

        var replyHead = new byte[4];
        await ReadExactAsync(stream, replyHead, ct);
        if (replyHead[0] != 5 || replyHead[1] != 0x00)
            throw new IOException($"上游 SOCKS5 连接失败（代码 0x{replyHead[1]:X2}）。");

        var addressLength = replyHead[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => (await ReadByteAsync(stream, ct)),
            _ => throw new IOException("上游 SOCKS5 返回未知地址类型。")
        };
        var discard = new byte[addressLength + 2];
        await ReadExactAsync(stream, discard, ct);
    }

    private static async Task<byte[]> ReadHttpHeaderAsync(Stream stream, int maxBytes,
        CancellationToken ct)
    {
        // 只读响应头，避免把客户端后续 TLS 数据提前吞掉。
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < maxBytes)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0)
                throw new IOException("上游代理在响应头结束前关闭连接。");
            buffer.WriteByte(one[0]);
            if (buffer.Length >= 4)
            {
                var data = buffer.GetBuffer();
                var end = (int)buffer.Length;
                if (data[end - 4] == 13 && data[end - 3] == 10 &&
                    data[end - 2] == 13 && data[end - 1] == 10)
                    return buffer.ToArray();
            }
        }
        throw new IOException("上游代理响应头超过限制。");
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                throw new IOException("上游代理连接提前关闭。");
            read += n;
        }
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        await ReadExactAsync(stream, one, ct);
        return one[0];
    }

    private static string FormatAuthority(string host, int port)
    {
        var value = IPAddress.TryParse(host, out var address) &&
                    address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;
        return value + ":" + port.ToString(CultureInfo.InvariantCulture);
    }
}
