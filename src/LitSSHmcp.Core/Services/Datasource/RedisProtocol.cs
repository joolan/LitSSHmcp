using System.Net.Sockets;
using System.Text;

namespace LitSSHmcp.Core.Services.Datasource;

public enum RedisValueKind
{
    SimpleString,
    Error,
    Integer,
    BulkString,
    Array,
    Null
}

/// <summary>RESP2 回复值（simple string / error / integer / bulk / array / null）。</summary>
public sealed class RedisValue
{
    public RedisValueKind Kind { get; }
    public string? Text { get; }
    public long Integer { get; }
    public IReadOnlyList<RedisValue>? Items { get; }

    private RedisValue(RedisValueKind kind, string? text = null, long integer = 0, IReadOnlyList<RedisValue>? items = null)
    {
        Kind = kind;
        Text = text;
        Integer = integer;
        Items = items;
    }

    public static RedisValue Simple(string text) => new(RedisValueKind.SimpleString, text);
    public static RedisValue Error(string message) => new(RedisValueKind.Error, message);
    public static RedisValue Number(long value) => new(RedisValueKind.Integer, integer: value);
    public static RedisValue Bulk(string text) => new(RedisValueKind.BulkString, text);
    public static RedisValue Array(IReadOnlyList<RedisValue> items) => new(RedisValueKind.Array, items: items);
    public static RedisValue Null { get; } = new(RedisValueKind.Null);

    public bool IsError => Kind == RedisValueKind.Error;
    public bool IsNull => Kind == RedisValueKind.Null;
    public string? ErrorMessage => IsError ? Text : null;

    /// <summary>标量值（非数组）转字符串，二进制内容用 base64 表示。</summary>
    public string ToDisplayString() => Kind switch
    {
        RedisValueKind.Integer => Integer.ToString(),
        RedisValueKind.Null => string.Empty,
        _ => Text ?? string.Empty
    };
}

/// <summary>RESP2 编解码（不依赖第三方 Redis 客户端库）。</summary>
public static class RespCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] EncodeCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            throw new ArgumentException("Redis 命令不能为空", nameof(args));

        var builder = new StringBuilder();
        builder.Append('*').Append(args.Count).Append("\r\n");
        foreach (var arg in args)
        {
            var value = arg ?? string.Empty;
            builder.Append('$').Append(Encoding.UTF8.GetByteCount(value)).Append("\r\n")
                   .Append(value).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    internal static string DecodeBulk(byte[] payload)
    {
        try
        {
            return StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return Convert.ToBase64String(payload);
        }
    }
}

/// <summary>从流中按 RESP2 协议读取一个回复。</summary>
public sealed class RespReader
{
    private readonly Stream _stream;
    private readonly byte[] _single = new byte[1];

    public RespReader(Stream stream)
    {
        _stream = stream;
    }

    public async Task<RedisValue> ReadAsync(CancellationToken ct = default)
    {
        var prefix = (char)await ReadByteAsync(ct);
        switch (prefix)
        {
            case '+':
                return RedisValue.Simple(await ReadLineAsync(ct));
            case '-':
                return RedisValue.Error(await ReadLineAsync(ct));
            case ':':
                return RedisValue.Number(ParseLong(await ReadLineAsync(ct)));
            case '$':
            {
                var length = ParseInt(await ReadLineAsync(ct));
                if (length < 0) return RedisValue.Null;
                var payload = await ReadExactAsync(length, ct);
                await ReadExactAsync(2, ct);
                return RedisValue.Bulk(RespCodec.DecodeBulk(payload));
            }
            case '*':
            {
                var count = ParseInt(await ReadLineAsync(ct));
                if (count < 0) return RedisValue.Null;
                var items = new List<RedisValue>(Math.Min(count, 4096));
                for (var i = 0; i < count; i++)
                    items.Add(await ReadAsync(ct));
                return RedisValue.Array(items);
            }
            default:
                throw new IOException($"非法的 RESP 响应前缀: 0x{(byte)prefix:X2} ('{prefix}')");
        }
    }

    private async Task<byte> ReadByteAsync(CancellationToken ct)
    {
        var read = await _stream.ReadAsync(_single.AsMemory(), ct);
        if (read == 0) throw new IOException("Redis 连接已关闭");
        return _single[0];
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var buffer = new MemoryStream(32);
        while (true)
        {
            var value = await ReadByteAsync(ct);
            if (value == (byte)'\r')
            {
                var next = await ReadByteAsync(ct);
                if (next != (byte)'\n')
                    throw new IOException("RESP 行结束符不正确(缺少 LF)");
                break;
            }
            buffer.WriteByte(value);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var payload = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await _stream.ReadAsync(payload.AsMemory(offset, count - offset), ct);
            if (read == 0) throw new IOException("Redis 连接在读取数据时被关闭");
            offset += read;
        }
        return payload;
    }

    private static long ParseLong(string text) =>
        long.TryParse(text, out var value) ? value : throw new IOException($"RESP 整数格式非法: '{text}'");

    private static int ParseInt(string text) =>
        int.TryParse(text, out var value) ? value : throw new IOException($"RESP 长度格式非法: '{text}'");
}

/// <summary>
/// 极简 Redis 客户端（RESP2 / TCP / 可选经 SSH 隧道），无第三方依赖。
/// 密码只发送给目标 Redis，不落在跳板机上。
/// </summary>
public sealed class RedisClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private bool _disposed;

    private RedisClient(TcpClient tcp, Stream stream)
    {
        _tcp = tcp;
        _stream = stream;
    }

    public static async Task<RedisClient> ConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await tcp.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            tcp.NoDelay = true;

            var stream = new BufferedStream(tcp.GetStream(), 32 * 1024);
            return new RedisClient(tcp, stream);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"连接 Redis {host}:{port} 超时({timeout.TotalSeconds:F0}s)");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task<RedisValue> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var payload = RespCodec.EncodeCommand(args);
        await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);

        return await new RespReader(_stream).ReadAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); } catch { /* ignore */ }
        try { _tcp.Dispose(); } catch { /* ignore */ }
    }
}
