using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>SOCKS5：方法协商、RFC 1929 用户名/密码、CONNECT（IPv4/域名/IPv6）。</summary>
internal static class Socks5ProxyConnection
{
    private const byte Version = 5;
    private const byte MethodNoAuth = 0x00;
    private const byte MethodUserPass = 0x02;
    private const byte MethodNone = 0xFF;
    private const byte CmdConnect = 0x01;
    private const byte AtypIpv4 = 0x01;
    private const byte AtypDomain = 0x03;
    private const byte AtypIpv6 = 0x04;

    public static async Task HandleAsync(TcpClient client, ProxyRuntime runtime, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint;
        var clientText = ProxyTunnel.EndpointText(remote);
        var clientAddress = remote is IPEndPoint ip ? ProxyPolicy.NormalizeAddress(ip.Address) : null;

        using var ownedClient = client;
        var stream = client.GetStream();

        if (!ProxyPolicy.IsClientAllowed(clientAddress, runtime.Options.AllowedClientCidrs))
        {
            runtime.RecordRequest();
            runtime.AddLog("SOCKS5", clientText, null, "-", "客户端白名单拒绝");
            return;
        }

        try
        {
            var greeting = new byte[2];
            if (!await ReadExactAsync(stream, greeting, ct) || greeting[0] != Version)
                return;

            var methodCount = greeting[1];
            if (methodCount == 0)
                return;
            var methods = new byte[methodCount];
            if (!await ReadExactAsync(stream, methods, ct))
                return;

            var supportsNoAuth = Array.IndexOf(methods, MethodNoAuth) >= 0;
            var supportsUserPass = Array.IndexOf(methods, MethodUserPass) >= 0;
            var selected = runtime.Options.AllowAnonymous && supportsNoAuth
                ? MethodNoAuth
                : supportsUserPass ? MethodUserPass : MethodNone;

            await stream.WriteAsync(new[] { Version, selected }, ct);
            if (selected == MethodNone)
            {
                runtime.RecordRequest();
                runtime.AddLog("SOCKS5", clientText, null, "-", "客户端不支持可用认证方式");
                return;
            }

            ProxyAccount? account = null;
            if (selected == MethodUserPass)
            {
                var authHead = new byte[2];
                if (!await ReadExactAsync(stream, authHead, ct) || authHead[0] != 0x01)
                    return;

                var usernameLength = authHead[1];
                if (usernameLength == 0)
                {
                    await WriteAuthFailureAsync(stream, ct);
                    runtime.RecordRequest();
                    runtime.AddLog("SOCKS5", clientText, null, "-", "认证失败");
                    return;
                }
                var usernameBytes = new byte[usernameLength];
                if (!await ReadExactAsync(stream, usernameBytes, ct))
                    return;

                var passwordLengthBytes = new byte[1];
                if (!await ReadExactAsync(stream, passwordLengthBytes, ct))
                    return;
                var passwordLength = passwordLengthBytes[0];
                var passwordBytes = new byte[passwordLength];
                if (passwordLength > 0 && !await ReadExactAsync(stream, passwordBytes, ct))
                    return;

                var username = Encoding.UTF8.GetString(usernameBytes);
                var password = Encoding.UTF8.GetString(passwordBytes);
                account = runtime.Authenticate(username, password);
                if (account is null)
                {
                    await WriteAuthFailureAsync(stream, ct);
                    runtime.RecordRequest();
                    runtime.AddLog("SOCKS5", clientText, null, "-", "认证失败");
                    return;
                }
                await stream.WriteAsync(new byte[] { 0x01, 0x00 }, ct);
            }

            var requestHead = new byte[4];
            if (!await ReadExactAsync(stream, requestHead, ct) || requestHead[0] != Version)
                return;
            var command = requestHead[1];
            var addressType = requestHead[3];

            string host;
            switch (addressType)
            {
                case AtypIpv4:
                {
                    var bytes = new byte[4];
                    if (!await ReadExactAsync(stream, bytes, ct))
                        return;
                    host = new IPAddress(bytes).ToString();
                    break;
                }
                case AtypIpv6:
                {
                    var bytes = new byte[16];
                    if (!await ReadExactAsync(stream, bytes, ct))
                        return;
                    host = new IPAddress(bytes).ToString();
                    break;
                }
                case AtypDomain:
                {
                    var lengthBytes = new byte[1];
                    if (!await ReadExactAsync(stream, lengthBytes, ct) || lengthBytes[0] == 0)
                    {
                        await WriteReplyAsync(stream, 0x08, null, ct);
                        return;
                    }
                    var domainBytes = new byte[lengthBytes[0]];
                    if (!await ReadExactAsync(stream, domainBytes, ct))
                        return;
                    host = Encoding.UTF8.GetString(domainBytes);
                    break;
                }
                default:
                    await WriteReplyAsync(stream, 0x08, null, ct);
                    return;
            }

            var portBytes = new byte[2];
            if (!await ReadExactAsync(stream, portBytes, ct))
                return;
            var port = (portBytes[0] << 8) | portBytes[1];

            runtime.RecordRequest();

            if (command != CmdConnect)
            {
                await WriteReplyAsync(stream, 0x07, null, ct);
                runtime.AddLog("SOCKS5", clientText, account?.Username, $"{host}:{port}",
                    $"不支持的命令 0x{command:X2}");
                return;
            }

            ProxyTargetDecision decision;
            try
            {
                decision = await ProxyPolicy.EvaluateAsync(host, port, runtime.Options, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!decision.Allowed)
            {
                await WriteReplyAsync(stream, 0x02, null, ct);
                runtime.AddLog("SOCKS5", clientText, account?.Username, $"{host}:{port}",
                    "策略拒绝: " + decision.Reason);
                return;
            }

            var connect = await ProxyTunnel.ConnectAsync(decision.Addresses, port,
                runtime.Options.ConnectTimeoutSeconds, ct);
            if (!connect.Success)
            {
                var reply = connect.Refused ? (byte)0x05 : connect.TimedOut ? (byte)0x06 : (byte)0x04;
                await WriteReplyAsync(stream, reply, null, ct);
                runtime.AddLog("SOCKS5", clientText, account?.Username, $"{host}:{port}",
                    "连接失败: " + (connect.Error ?? "未知错误"));
                return;
            }

            using var upstream = connect.Client!;
            var upstreamStream = upstream.GetStream();
            await WriteReplyAsync(stream, 0x00,
                upstream.Client.LocalEndPoint as IPEndPoint, ct);

            var (up, down) = await ProxyTunnel.RelayAsync(stream, upstreamStream,
                runtime.Options.IdleTimeoutSeconds, runtime.Options.MaxConnectionMinutes, ct);
            runtime.RecordTraffic(account, up, down);
            runtime.AddLog("SOCKS5", clientText, account?.Username, $"{host}:{port}",
                "CONNECT 成功", up, down);
        }
        catch (OperationCanceledException)
        {
            // 停止代理或空闲超时
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
        {
            runtime.AddLog("SOCKS5", clientText, null, "-", "连接中断: " + ex.Message);
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer,
        CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    private static async Task WriteAuthFailureAsync(Stream stream, CancellationToken ct) =>
        await stream.WriteAsync(new byte[] { 0x01, 0x01 }, ct);

    private static async Task WriteReplyAsync(Stream stream, byte reply,
        IPEndPoint? bind, CancellationToken ct)
    {
        var address = bind?.Address is { } value ? ProxyPolicy.NormalizeAddress(value) : IPAddress.Any;
        var addressBytes = address.GetAddressBytes();
        var atyp = address.AddressFamily == AddressFamily.InterNetworkV6 ? AtypIpv6 : AtypIpv4;
        if (atyp == AtypIpv4 && addressBytes.Length != 4)
        {
            address = IPAddress.Any;
            addressBytes = address.GetAddressBytes();
        }

        var port = bind?.Port ?? 0;
        var response = new byte[4 + addressBytes.Length + 2];
        response[0] = Version;
        response[1] = reply;
        response[2] = 0x00;
        response[3] = atyp;
        Buffer.BlockCopy(addressBytes, 0, response, 4, addressBytes.Length);
        response[^2] = (byte)(port >> 8);
        response[^1] = (byte)(port & 0xFF);
        await stream.WriteAsync(response, ct);
    }
}
