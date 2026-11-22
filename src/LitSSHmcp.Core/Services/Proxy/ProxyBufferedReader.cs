using System.Globalization;
using System.Text;

namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>
/// 带缓冲的流读取器：解析完请求头后仍保留已读入的正文/隧道字节。
/// 不能直接用 StreamReader，否则会吞掉 CONNECT 之后的 TLS 流量。
/// </summary>
internal sealed class ProxyBufferedReader
{
    private static readonly byte[] CrLf = { 13, 10 };
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _offset;
    private int _count;

    public ProxyBufferedReader(Stream stream, int bufferSize = 8192)
    {
        _stream = stream;
        _buffer = new byte[Math.Max(512, bufferSize)];
    }

    private int Buffered => _count - _offset;

    /// <summary>读取到 \r\n\r\n（不包含分隔符）；超过上限、EOF 或格式不完整返回 null。</summary>
    public async ValueTask<byte[]?> ReadHeaderBlockAsync(int maxBytes, CancellationToken ct)
    {
        using var collected = new MemoryStream();
        while (true)
        {
            for (var i = _offset; i + 3 < _count; i++)
            {
                if (_buffer[i] != 13 || _buffer[i + 1] != 10 ||
                    _buffer[i + 2] != 13 || _buffer[i + 3] != 10)
                    continue;

                var take = i - _offset;
                if (collected.Length + take > maxBytes)
                    return null;
                collected.Write(_buffer, _offset, take);
                _offset = i + 4;
                return collected.ToArray();
            }

            if (_count > _offset)
            {
                var available = _count - _offset;
                // 保留最后 3 个字节，避免 \r\n\r\n 恰好跨两次读取时漏检。
                var append = Math.Max(0, available - 3);
                if (append > 0)
                {
                    if (collected.Length + append > maxBytes)
                        return null;
                    collected.Write(_buffer, _offset, append);
                    _offset += append;
                }

                var remaining = _count - _offset;
                if (remaining > 0 && _offset > 0)
                {
                    Buffer.BlockCopy(_buffer, _offset, _buffer, 0, remaining);
                    _offset = 0;
                    _count = remaining;
                }
            }

            if (!await FillAsync(ct))
                return null;
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
    {
        if (destination.Length == 0)
            return 0;
        if (Buffered == 0 && !await FillAsync(ct))
            return 0;

        var take = Math.Min(destination.Length, Buffered);
        _buffer.AsMemory(_offset, take).CopyTo(destination);
        _offset += take;
        return take;
    }

    public async ValueTask ReadExactAsync(Memory<byte> destination, CancellationToken ct)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = await ReadAsync(destination[read..], ct);
            if (n == 0)
                throw new EndOfStreamException("连接在读取完整数据前关闭。");
            read += n;
        }
    }

    /// <summary>读取一行（不含 CRLF），用于 SOCKS5 与 chunked 编码。</summary>
    public async ValueTask<byte[]?> ReadLineAsync(int maxLength, CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            var value = await ReadByteAsync(ct);
            if (value < 0)
                return line.Length == 0 ? null : throw new EndOfStreamException("连接在行结束前关闭。");
            if (value == '\n')
            {
                var data = line.ToArray();
                if (data.Length > 0 && data[^1] == '\r')
                    Array.Resize(ref data, data.Length - 1);
                return data;
            }
            if (line.Length >= maxLength)
                throw new InvalidDataException("行长度超过限制。");
            line.WriteByte((byte)value);
        }
    }

    public async ValueTask<long> CopyExactAsync(Stream destination, long count, CancellationToken ct,
        Action? onActivity = null)
    {
        var buffer = new byte[8192];
        var remaining = count;
        long total = 0;
        while (remaining > 0)
        {
            var n = await ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (n == 0)
                throw new EndOfStreamException("连接在请求体传输完成前关闭。");
            await destination.WriteAsync(buffer.AsMemory(0, n), ct);
            onActivity?.Invoke();
            remaining -= n;
            total += n;
        }
        return total;
    }

    /// <summary>原样转发 chunked 正文（包括 chunk 大小、扩展与 trailer）。</summary>
    public async ValueTask<long> CopyChunkedAsync(Stream destination, CancellationToken ct,
        Action? onActivity = null)
    {
        long total = 0;
        while (true)
        {
            var line = await ReadLineAsync(8192, ct)
                ?? throw new EndOfStreamException("chunked 正文不完整。");
            await destination.WriteAsync(line, ct);
            await destination.WriteAsync(CrLf, ct);
            onActivity?.Invoke();
            total += line.Length + 2;

            var text = Encoding.ASCII.GetString(line);
            var semicolon = text.IndexOf(';');
            var hex = (semicolon >= 0 ? text[..semicolon] : text).Trim();
            if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) ||
                size < 0)
                throw new InvalidDataException("非法的 chunk 大小。");

            if (size == 0)
            {
                while (true)
                {
                    var trailer = await ReadLineAsync(8192, ct)
                        ?? throw new EndOfStreamException("chunked trailer 不完整。");
                    await destination.WriteAsync(trailer, ct);
                    await destination.WriteAsync(CrLf, ct);
                    onActivity?.Invoke();
                    total += trailer.Length + 2;
                    if (trailer.Length == 0)
                        return total;
                }
            }

            total += await CopyExactAsync(destination, size, ct, onActivity);
            var crlf = new byte[2];
            await ReadExactAsync(crlf, ct);
            await destination.WriteAsync(crlf, ct);
            onActivity?.Invoke();
            total += 2;
        }
    }

    public async ValueTask<long> CopyUntilEofAsync(Stream destination, CancellationToken ct,
        Action? onActivity = null)
    {
        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            var n = await ReadAsync(buffer, ct);
            if (n == 0)
                return total;
            await destination.WriteAsync(buffer.AsMemory(0, n), ct);
            onActivity?.Invoke();
            total += n;
        }
    }

    /// <summary>把已经预读的字节写入目标流（不阻塞等待新数据）。</summary>
    public async ValueTask FlushBufferedAsync(Stream destination, CancellationToken ct)
    {
        while (Buffered > 0)
        {
            var take = Math.Min(Buffered, 8192);
            await destination.WriteAsync(_buffer.AsMemory(_offset, take), ct);
            _offset += take;
        }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        if (Buffered == 0 && !await FillAsync(ct))
            return -1;
        return _buffer[_offset++];
    }

    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        if (_offset > 0 && _offset == _count)
        {
            _offset = 0;
            _count = 0;
        }

        if (_count == _buffer.Length)
        {
            if (_offset == 0)
                return false;
            Buffer.BlockCopy(_buffer, _offset, _buffer, 0, _count - _offset);
            _count -= _offset;
            _offset = 0;
        }

        var read = await _stream.ReadAsync(
            _buffer.AsMemory(_count, _buffer.Length - _count), ct);
        if (read == 0)
            return false;
        _count += read;
        return true;
    }
}
