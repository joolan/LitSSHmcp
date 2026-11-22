using System.Net;
using System.Net.Sockets;

namespace LitSSHmcp.Core.Services.Proxy;

internal sealed record ProxyConnectResult(
    TcpClient? Client,
    bool TimedOut,
    bool Refused,
    string? Error)
{
    public bool Success => Client is not null;
}

/// <summary>原始 TCP 隧道与目标连接辅助。</summary>
internal static class ProxyTunnel
{
    public static async Task<ProxyConnectResult> ConnectAsync(
        IPAddress[] addresses, int port, int timeoutSeconds, CancellationToken ct)
    {
        var timeout = Math.Clamp(timeoutSeconds, 1, 120);
        var timedOut = false;
        var refused = false;
        string? lastError = null;

        foreach (var address in addresses)
        {
            var client = new TcpClient(address.AddressFamily);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(timeout));
            try
            {
                await client.ConnectAsync(address, port, attempt.Token);
                return new ProxyConnectResult(client, false, false, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                lastError = "连接目标超时。";
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.ConnectionRefused)
                    refused = true;
                lastError = ex.Message;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
            client.Dispose();
        }

        return new ProxyConnectResult(null, timedOut, refused, lastError);
    }

    /// <summary>
    /// 双向字节转发。任一侧结束即关闭双方；共享空闲计时器，任一方向有数据都会重置。
    /// 返回（客户端->目标, 目标->客户端）字节数。
    /// </summary>
    public static async Task<(long ClientToTarget, long TargetToClient)> RelayAsync(
        Stream client, Stream target, int idleSeconds, int maxMinutes, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(maxMinutes, 1, 1440)));
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var idleTimeout = TimeSpan.FromSeconds(Math.Clamp(idleSeconds, 5, 3600));
        idle.CancelAfter(idleTimeout);

        long up = 0;
        long down = 0;
        var upTask = CopyAsync(client, target, count =>
        {
            Interlocked.Add(ref up, count);
            Reset(idle, idleTimeout);
        }, idle.Token);
        var downTask = CopyAsync(target, client, count =>
        {
            Interlocked.Add(ref down, count);
            Reset(idle, idleTimeout);
        }, idle.Token);

        await Task.WhenAny(upTask, downTask);
        try { idle.Cancel(); } catch { /* ignore */ }
        try { await Task.WhenAll(upTask, downTask); } catch { /* 任一侧断开即结束 */ }
        return (up, down);
    }

    public static string EndpointText(EndPoint? endpoint) => endpoint switch
    {
        IPEndPoint ip => $"{ip.Address}:{ip.Port}",
        _ => endpoint?.ToString() ?? "-"
    };

    public static string? ClientKey(EndPoint? endpoint) =>
        endpoint is IPEndPoint ip ? ProxyPolicy.NormalizeAddress(ip.Address).ToString() : null;

    private static async Task CopyAsync(Stream source, Stream destination,
        Action<int> onData, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (read == 0)
                return;

            onData(read);
            try
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private static void Reset(CancellationTokenSource source, TimeSpan delay)
    {
        try { source.CancelAfter(delay); }
        catch (ObjectDisposedException) { /* 连接已结束 */ }
    }
}
