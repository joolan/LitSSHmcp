using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LitSSHmcp.Core.Services.Jenkins;

/// <summary>连接本机动态 SOCKS5 转发；用于在不改写 Jenkins 原始 URL 的情况下穿透 SSH。</summary>
public static class Socks5Connector
{
    public static async ValueTask<Stream> ConnectAsync(
        string proxyHost,
        int proxyPort,
        string targetHost,
        int targetPort,
        CancellationToken ct = default)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        NetworkStream? stream = null;
        try
        {
            await socket.ConnectAsync(new DnsEndPoint(proxyHost, proxyPort), ct).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct).ConfigureAwait(false);
            var greeting = new byte[2];
            await ReadExactlyAsync(stream, greeting, ct).ConfigureAwait(false);
            if (greeting[0] != 0x05 || greeting[1] != 0x00)
                throw new IOException("SOCKS5 代理不接受无认证方式。");

            var hostBytes = Encoding.UTF8.GetBytes(targetHost);
            if (hostBytes.Length is 0 or > 255)
                throw new IOException("SOCKS5 目标主机名长度无效。");

            var request = new byte[7 + hostBytes.Length];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = 0x03;
            request[4] = (byte)hostBytes.Length;
            hostBytes.CopyTo(request, 5);
            request[^2] = (byte)(targetPort >> 8);
            request[^1] = (byte)(targetPort & 0xFF);

            await stream.WriteAsync(request, ct).ConfigureAwait(false);
            var reply = new byte[4];
            await ReadExactlyAsync(stream, reply, ct).ConfigureAwait(false);
            if (reply[0] != 0x05 || reply[1] != 0x00)
                throw new IOException($"SOCKS5 连接目标失败（code={reply[1]}）。");

            var addressLength = reply[3] switch
            {
                0x01 => 4,
                0x03 => await ReadOneAsync(stream, ct).ConfigureAwait(false),
                0x04 => 16,
                _ => throw new IOException("SOCKS5 返回了不支持的目标地址类型。")
            };

            var discard = new byte[addressLength + 2];
            await ReadExactlyAsync(stream, discard, ct).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            try { stream?.Dispose(); } catch { /* ignore */ }
            try { socket.Dispose(); } catch { /* ignore */ }
            throw;
        }
    }

    private static async Task<int> ReadOneAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        await ReadExactlyAsync(stream, buffer, ct).ConfigureAwait(false);
        return buffer[0];
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("SOCKS5 代理连接已关闭。");
            offset += read;
        }
    }
}
