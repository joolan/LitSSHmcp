using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>HTTP 正向代理：CONNECT 隧道与绝对 URI 的普通 HTTP 转发。</summary>
internal static class HttpProxyConnection
{
    private static readonly byte[] HeaderTerminator = { 13, 10, 13, 10 };

    public static async Task HandleAsync(TcpClient client, ProxyRuntime runtime, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint;
        var clientText = ProxyTunnel.EndpointText(remote);
        var clientAddress = remote is IPEndPoint ip ? ProxyPolicy.NormalizeAddress(ip.Address) : null;

        using var ownedClient = client;
        var stream = client.GetStream();
        var reader = new ProxyBufferedReader(stream);

        if (!ProxyPolicy.IsClientAllowed(clientAddress, runtime.Options.AllowedClientCidrs))
        {
            runtime.RecordRequest();
            runtime.AddLog("HTTP", clientText, null, "-", "客户端白名单拒绝");
            await WriteErrorAsync(stream, 403, "Forbidden", "客户端地址不在白名单内。", ct);
            return;
        }

        byte[]? headerBytes;
        try
        {
            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headerCts.CancelAfter(TimeSpan.FromSeconds(runtime.Options.HeaderTimeoutSeconds));
            headerBytes = await reader.ReadHeaderBlockAsync(16 * 1024, headerCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            return;
        }

        if (headerBytes is null)
        {
            runtime.RecordRequest();
            runtime.AddLog("HTTP", clientText, null, "-", "请求头无效或超时");
            await WriteErrorAsync(stream, 400, "Bad Request", "请求头无效或超时。", ct);
            return;
        }

        var lines = Encoding.Latin1.GetString(headerBytes)
            .Split("\r\n", StringSplitOptions.None);
        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            runtime.RecordRequest();
            runtime.AddLog("HTTP", clientText, null, "-", "请求行无效");
            await WriteErrorAsync(stream, 400, "Bad Request", "请求行无效。", ct);
            return;
        }

        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        var version = parts[2];
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
                break;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            headers.Add(new KeyValuePair<string, string>(
                line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        string? Header(string name)
        {
            foreach (var header in headers)
            {
                if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                    return header.Value;
            }
            return null;
        }

        runtime.RecordRequest();

        if (!TryAuthenticate(Header("Proxy-Authorization"), runtime, out var account))
        {
            runtime.AddLog("HTTP", clientText, null, "-", "认证失败");
            await WriteErrorAsync(stream, 407, "Proxy Authentication Required",
                "需要代理账号与密码。", ct,
                "Proxy-Authenticate: Basic realm=\"LitSSH LAN Proxy\"\r\n");
            return;
        }

        string host;
        int port;
        string pathAndQuery;
        if (method == "CONNECT")
        {
            if (!TryParseAuthority(target, 443, out host, out port))
            {
                runtime.AddLog("HTTP", clientText, account?.Username, target, "CONNECT 地址无效");
                await WriteErrorAsync(stream, 400, "Bad Request", "CONNECT 目标地址无效。", ct);
                return;
            }
            pathAndQuery = string.Empty;
        }
        else
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                runtime.AddLog("HTTP", clientText, account?.Username, target, "非绝对 HTTP URI");
                await WriteErrorAsync(stream, 400, "Bad Request",
                    "普通 HTTP 代理只接受 http:// 绝对 URI；HTTPS 请使用 CONNECT。", ct);
                return;
            }
            host = uri.DnsSafeHost;
            port = uri.IsDefaultPort ? 80 : uri.Port;
            pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
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
            runtime.AddLog("HTTP", clientText, account?.Username, $"{host}:{port}",
                "策略拒绝: " + decision.Reason);
            await WriteErrorAsync(stream, 403, "Forbidden", decision.Reason, ct);
            return;
        }

        var connect = await ProxyTunnel.ConnectAsync(decision.Addresses, port,
            runtime.Options.ConnectTimeoutSeconds, ct);
        if (!connect.Success)
        {
            var status = connect.TimedOut ? 504 : 502;
            var reason = connect.TimedOut ? "Gateway Timeout" : "Bad Gateway";
            runtime.AddLog("HTTP", clientText, account?.Username, $"{host}:{port}",
                "连接失败: " + (connect.Error ?? "未知错误"));
            await WriteErrorAsync(stream, status, reason, connect.Error ?? "连接目标失败。", ct);
            return;
        }

        using var upstream = connect.Client!;
        var upstreamStream = upstream.GetStream();

        if (method == "CONNECT")
        {
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 Connection Established\r\nProxy-Agent: LitSSH\r\n\r\n"), ct);
                await reader.FlushBufferedAsync(upstreamStream, ct);
                var (up, down) = await ProxyTunnel.RelayAsync(stream, upstreamStream,
                    runtime.Options.IdleTimeoutSeconds, runtime.Options.MaxConnectionMinutes, ct);
                runtime.RecordTraffic(account, up, down);
                runtime.AddLog("HTTP", clientText, account?.Username, $"{host}:{port}",
                    "CONNECT 成功", up, down);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                runtime.AddLog("HTTP", clientText, account?.Username, $"{host}:{port}", "CONNECT 中断");
            }
            return;
        }

        await ForwardHttpAsync(stream, upstreamStream, reader,
            method, version, host, port, pathAndQuery, headers, account, clientText, runtime, ct);
    }

    private static async Task ForwardHttpAsync(
        Stream clientStream,
        Stream upstreamStream,
        ProxyBufferedReader reader,
        string method,
        string version,
        string host,
        int port,
        string pathAndQuery,
        List<KeyValuePair<string, string>> headers,
        ProxyAccount? account,
        string clientText,
        ProxyRuntime runtime,
        CancellationToken ct)
    {
        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var idle = TimeSpan.FromSeconds(runtime.Options.IdleTimeoutSeconds);
        bodyCts.CancelAfter(idle);
        void Touch()
        {
            try { bodyCts.CancelAfter(idle); }
            catch (ObjectDisposedException) { /* 已结束 */ }
        }

        var request = new StringBuilder(512);
        request.Append(method).Append(' ').Append(pathAndQuery).Append(' ').Append(version).Append("\r\n");
        var hasChunked = HeaderValue(headers, "Transfer-Encoding")
            ?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;
        var expectsContinue = HeaderValue(headers, "Expect")
            ?.Contains("100-continue", StringComparison.OrdinalIgnoreCase) == true;
        if (!hasChunked &&
            !string.IsNullOrWhiteSpace(HeaderValue(headers, "Transfer-Encoding")))
        {
            runtime.AddLog("HTTP", clientText, account?.Username, $"{host}:{port}",
                "不支持的 Transfer-Encoding");
            await WriteErrorAsync(clientStream, 501, "Not Implemented",
                "仅支持 Transfer-Encoding: chunked。", ct);
            return;
        }

        foreach (var header in headers)
        {
            if (IsHopByHop(header.Key) ||
                string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Expect", StringComparison.OrdinalIgnoreCase) ||
                (hasChunked && string.Equals(header.Key, "Content-Length",
                    StringComparison.OrdinalIgnoreCase)))
                continue;
            request.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }
        request.Append("Host: ").Append(FormatHostForHeader(host));
        if (port != 80)
            request.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        request.Append("\r\nConnection: close\r\n\r\n");

        long requestBodyBytes = 0;
        try
        {
            await upstreamStream.WriteAsync(Encoding.Latin1.GetBytes(request.ToString()), bodyCts.Token);
            if (expectsContinue)
                await clientStream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 100 Continue\r\n\r\n"), bodyCts.Token);

            if (hasChunked)
            {
                requestBodyBytes = await reader.CopyChunkedAsync(upstreamStream, bodyCts.Token, Touch);
            }
            else if (long.TryParse(HeaderValue(headers, "Content-Length"),
                         NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length > 0)
            {
                requestBodyBytes = await reader.CopyExactAsync(upstreamStream, length, bodyCts.Token, Touch);
            }

            var upstreamReader = new ProxyBufferedReader(upstreamStream);
            byte[]? responseHeader;
            while (true)
            {
                using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                responseCts.CancelAfter(TimeSpan.FromSeconds(runtime.Options.HeaderTimeoutSeconds));
                responseHeader = await upstreamReader.ReadHeaderBlockAsync(32 * 1024, responseCts.Token);
                if (responseHeader is null)
                {
                    runtime.AddLog("HTTP", clientText, account?.Username, $"{host}:{port}",
                        "目标无响应");
                    return;
                }

                var responseLines = Encoding.Latin1.GetString(responseHeader)
                    .Split("\r\n", StringSplitOptions.None);
                var statusParts = responseLines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out var statusCode))
                {
                    runtime.AddLog("HTTP", clientText, account?.Username, $"{host}:{port}",
                        "目标响应无效");
                    return;
                }

                if (statusCode is >= 100 and < 200)
                {
                    // 转发 100 Continue 等临时响应，然后继续读取最终响应。
                    await clientStream.WriteAsync(responseHeader, ct);
                    await clientStream.WriteAsync(HeaderTerminator, ct);
                    continue;
                }

                var forwarded = new StringBuilder(512);
                forwarded.Append(responseLines[0]).Append("\r\n");
                var responseChunked = HeaderValue(responseLines, "Transfer-Encoding")
                    ?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;
                foreach (var line in responseLines.Skip(1))
                {
                    if (line.Length == 0)
                        break;
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                        continue;
                    var name = line[..colon].Trim();
                    if (string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                        (responseChunked && string.Equals(name, "Content-Length",
                            StringComparison.OrdinalIgnoreCase)) ||
                        (!responseChunked && string.Equals(name, "Transfer-Encoding",
                            StringComparison.OrdinalIgnoreCase)))
                        continue;
                    forwarded.Append(line).Append("\r\n");
                }
                forwarded.Append("Connection: close\r\n\r\n");
                await clientStream.WriteAsync(Encoding.Latin1.GetBytes(forwarded.ToString()), ct);

                long responseBodyBytes = 0;
                if (!string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) &&
                    statusCode is not (204 or 304))
                {
                    if (responseChunked)
                    {
                        responseBodyBytes = await upstreamReader.CopyChunkedAsync(
                            clientStream, bodyCts.Token, Touch);
                    }
                    else if (long.TryParse(HeaderValue(responseLines, "Content-Length"),
                                 NumberStyles.Integer, CultureInfo.InvariantCulture, out var bodyLength) &&
                             bodyLength >= 0)
                    {
                        responseBodyBytes = await upstreamReader.CopyExactAsync(
                            clientStream, bodyLength, bodyCts.Token, Touch);
                    }
                    else
                    {
                        responseBodyBytes = await upstreamReader.CopyUntilEofAsync(
                            clientStream, bodyCts.Token, Touch);
                    }
                }

                runtime.RecordTraffic(account, requestBodyBytes, responseBodyBytes);
                runtime.AddLog("HTTP", clientText, account?.Username, $"{host}:{port}",
                    $"HTTP {statusCode}", requestBodyBytes, responseBodyBytes);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            runtime.RecordTraffic(account, requestBodyBytes, 0);
            runtime.AddLog("HTTP", clientText, account?.Username, $"{host}:{port}", "超时或取消");
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            runtime.RecordTraffic(account, requestBodyBytes, 0);
            runtime.AddLog("HTTP", clientText, account?.Username, $"{host}:{port}",
                "转发中断: " + ex.Message);
            return;
        }
    }

    private static bool TryAuthenticate(string? authorization, ProxyRuntime runtime,
        out ProxyAccount? account)
    {
        account = null;
        if (string.IsNullOrWhiteSpace(authorization))
            return runtime.Options.AllowAnonymous;

        const string prefix = "Basic ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(authorization[prefix.Length..].Trim()));
            var colon = decoded.IndexOf(':');
            if (colon < 0)
                return false;
            var username = decoded[..colon];
            var password = decoded[(colon + 1)..];
            account = runtime.Authenticate(username, password);
            return account is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseAuthority(string value, int defaultPort,
        out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value[0] == '[')
        {
            var close = value.IndexOf(']');
            if (close <= 1)
                return false;
            host = value[1..close];
            if (close + 1 < value.Length)
            {
                if (value[close + 1] != ':' ||
                    !int.TryParse(value[(close + 2)..], out port))
                    return false;
            }
            return true;
        }

        var first = value.IndexOf(':');
        var last = value.LastIndexOf(':');
        if (first >= 0 && first == last)
        {
            host = value[..first];
            return int.TryParse(value[(first + 1)..], out port) && !string.IsNullOrWhiteSpace(host);
        }

        host = value;
        return !string.IsNullOrWhiteSpace(host);
    }

    private static bool IsHopByHop(string name) =>
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static string FormatHostForHeader(string host) =>
        IPAddress.TryParse(host, out var address) &&
        address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;

    private static string? HeaderValue(List<KeyValuePair<string, string>> headers, string name)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                return header.Value;
        }
        return null;
    }

    private static string? HeaderValue(string[] headerLines, string name)
    {
        foreach (var line in headerLines.Skip(1))
        {
            if (line.Length == 0)
                break;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            if (string.Equals(line[..colon].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    private static async Task WriteErrorAsync(Stream stream, int status, string reason,
        string message, CancellationToken ct, string? extraHeaders = null)
    {
        var body = Encoding.UTF8.GetBytes(message + "\r\n");
        var header = $"HTTP/1.1 {status} {reason}\r\n" +
                     "Content-Type: text/plain; charset=utf-8\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n" +
                     (extraHeaders ?? string.Empty) +
                     "\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.WriteAsync(body, ct);
        }
        catch
        {
            // 客户端可能已断开
        }
    }
}
