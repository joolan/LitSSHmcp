using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace LitSSHmcp.Core.Services.DnsServer;

/// <summary>DNS 问题段（只支持单问题报文）。</summary>
public sealed record DnsQuestion(
    string Name,
    ushort Type,
    ushort Class,
    int NameOffset,
    int EndOffset,
    bool HasCompression);

/// <summary>资源记录的定位信息；TTL 偏移用于缓存命中时递减 TTL。</summary>
public readonly record struct DnsRecordSpan(
    ushort Type,
    ushort Class,
    uint Ttl,
    int TtlOffset,
    int RdataOffset,
    int RdataLength);

/// <summary>一条待写入响应的资源记录。</summary>
public readonly record struct DnsAnswerRecord(ushort Type, uint Ttl, byte[] Rdata);

/// <summary>解析后的 DNS 报文布局。</summary>
public sealed record DnsMessageLayout(
    ushort Id,
    ushort Flags,
    DnsQuestion Question,
    IReadOnlyList<DnsRecordSpan> Answers,
    IReadOnlyList<DnsRecordSpan> Authorities,
    IReadOnlyList<DnsRecordSpan> Additionals)
{
    public int Rcode => Flags & 0x0F;
    public bool IsResponse => (Flags & 0x8000) != 0;
    public bool IsAuthoritative => (Flags & 0x0400) != 0;
    public bool Truncated => (Flags & 0x0200) != 0;

    public DnsRecordSpan? Opt =>
        Additionals.FirstOrDefault(record => record.Type == 41);

    public bool HasOpt => Additionals.Any(record => record.Type == 41);

    public ushort UdpPayloadSize
    {
        get
        {
            var opt = Opt;
            return opt is { } value ? Math.Max((ushort)512, value.Class) : (ushort)512;
        }
    }

    public bool DnssecOk
    {
        get
        {
            var opt = Opt;
            return opt is { } value && (value.Ttl & 0x8000) != 0;
        }
    }

    public bool OptHasOptions
    {
        get
        {
            var opt = Opt;
            return opt is { } value && value.RdataLength > 0;
        }
    }

    public IReadOnlyList<int> TtlOffsets =>
        Answers.Concat(Authorities).Concat(Additionals)
            .Where(record => record.Type != 41)
            .Select(record => record.TtlOffset)
            .ToArray();

    public uint MinTtl
    {
        get
        {
            var values = Answers.Concat(Authorities).Concat(Additionals)
                .Where(record => record.Type != 41)
                .Select(record => record.Ttl)
                .ToArray();
            return values.Length == 0 ? 0 : values.Min();
        }
    }
}

/// <summary>
/// 最小 DNS 线格式工具：解析头部/问题/资源记录，原样转发响应，构造本地响应。
/// 只解析缓存和转发需要的字段，不重写未知记录类型，避免破坏 HTTPS/SVCB/RRSIG。
/// </summary>
public static class DnsWire
{
    public const int HeaderLength = 12;

    public static ushort ReadU16(ReadOnlySpan<byte> buffer, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));

    public static uint ReadU32(ReadOnlySpan<byte> buffer, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset, 4));

    public static void WriteU16(Span<byte> buffer, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset, 2), value);

    public static void WriteU32(Span<byte> buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(offset, 4), value);

    public static string NormalizeName(string? value) =>
        (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    public static bool IsValidRecordName(string? value)
    {
        var name = NormalizeName(value);
        if (name.Length == 0 || name.Length > 253)
            return false;

        var labels = name.Split('.');
        if (labels.Length == 1 && labels[0] == "*")
            return false;

        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            if (label.Length == 0 || label.Length > 63)
                return false;
            if (label == "*" && i == 0)
                continue;
            foreach (var c in label)
            {
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                    return false;
            }
            if (label[0] == '-' || label[^1] == '-')
                return false;
        }
        return true;
    }

    public static byte[] EncodeName(string value)
    {
        var name = NormalizeName(value);
        using var stream = new MemoryStream();
        if (name.Length > 0)
        {
            foreach (var label in name.Split('.'))
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                if (bytes.Length is 0 or > 63)
                    throw new ArgumentException("域名标签长度不合法：" + value);
                stream.WriteByte((byte)bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        stream.WriteByte(0);
        return stream.ToArray();
    }

    public static bool TryReadName(ReadOnlySpan<byte> message, int start, out string name,
        out int nextOffset, out bool usedCompression)
    {
        name = string.Empty;
        nextOffset = start;
        usedCompression = false;
        var labels = new List<string>(8);
        var offset = start;
        var next = -1;
        var jumps = 0;
        var totalLength = 0;

        while (true)
        {
            if (offset < 0 || offset >= message.Length)
                return false;

            var length = message[offset];
            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= message.Length)
                    return false;
                var pointer = ((length & 0x3F) << 8) | message[offset + 1];
                if (next < 0)
                    next = offset + 2;
                if (pointer >= message.Length || ++jumps > 32)
                    return false;
                usedCompression = true;
                offset = pointer;
                continue;
            }

            if ((length & 0xC0) != 0)
                return false;

            offset++;
            if (length == 0)
            {
                if (next < 0)
                    next = offset;
                break;
            }

            if (offset + length > message.Length)
                return false;
            totalLength += length + 1;
            if (totalLength > 255)
                return false;

            labels.Add(Encoding.ASCII.GetString(message.Slice(offset, length)));
            offset += length;
        }

        name = string.Join('.', labels);
        nextOffset = next;
        return true;
    }

    public static bool TrySkipName(ReadOnlySpan<byte> message, ref int offset)
    {
        while (true)
        {
            if (offset < 0 || offset >= message.Length)
                return false;

            var length = message[offset];
            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= message.Length)
                    return false;
                offset += 2;
                return true;
            }

            if ((length & 0xC0) != 0)
                return false;

            offset++;
            if (length == 0)
                return true;
            if (offset + length > message.Length)
                return false;
            offset += length;
        }
    }

    public static bool TryParseMessage(ReadOnlySpan<byte> message, out DnsMessageLayout layout)
    {
        layout = null!;
        if (message.Length < HeaderLength)
            return false;

        var id = ReadU16(message, 0);
        var flags = ReadU16(message, 2);
        var questionCount = ReadU16(message, 4);
        var answerCount = ReadU16(message, 6);
        var authorityCount = ReadU16(message, 8);
        var additionalCount = ReadU16(message, 10);
        if (questionCount != 1)
            return false;

        var offset = HeaderLength;
        if (!TryReadName(message, offset, out var name, out var nameEnd, out var compressed))
            return false;
        if (nameEnd + 4 > message.Length)
            return false;

        var type = ReadU16(message, nameEnd);
        var @class = ReadU16(message, nameEnd + 2);
        var questionEnd = nameEnd + 4;
        var question = new DnsQuestion(NormalizeName(name), type, @class,
            HeaderLength, questionEnd, compressed);
        offset = questionEnd;

        if (!TryReadRecords(message, ref offset, answerCount, out var answers))
            return false;
        if (!TryReadRecords(message, ref offset, authorityCount, out var authorities))
            return false;
        if (!TryReadRecords(message, ref offset, additionalCount, out var additionals))
            return false;

        layout = new DnsMessageLayout(id, flags, question, answers, authorities, additionals);
        return true;
    }

    private static bool TryReadRecords(ReadOnlySpan<byte> message, ref int offset, int count,
        out IReadOnlyList<DnsRecordSpan> records)
    {
        var list = new List<DnsRecordSpan>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            if (!TrySkipName(message, ref offset))
            {
                records = Array.Empty<DnsRecordSpan>();
                return false;
            }
            if (offset + 10 > message.Length)
            {
                records = Array.Empty<DnsRecordSpan>();
                return false;
            }

            var type = ReadU16(message, offset);
            var @class = ReadU16(message, offset + 2);
            var ttlOffset = offset + 4;
            var ttl = ReadU32(message, ttlOffset);
            var dataLength = ReadU16(message, offset + 8);
            var dataOffset = offset + 10;
            if (dataOffset + dataLength > message.Length)
            {
                records = Array.Empty<DnsRecordSpan>();
                return false;
            }

            list.Add(new DnsRecordSpan(type, @class, ttl, ttlOffset, dataOffset, dataLength));
            offset = dataOffset + dataLength;
        }

        records = list;
        return true;
    }

    public static uint GetNegativeTtl(ReadOnlySpan<byte> message, DnsMessageLayout layout, uint fallback)
    {
        var soa = layout.Authorities.FirstOrDefault(record => record.Type == 6);
        if (soa.Type != 6)
            return fallback;

        var offset = soa.RdataOffset;
        if (!TrySkipName(message, ref offset) || !TrySkipName(message, ref offset))
            return fallback;
        var end = soa.RdataOffset + soa.RdataLength;
        if (offset + 20 > end)
            return fallback;

        var minimum = ReadU32(message, offset + 16);
        return Math.Min(soa.Ttl, minimum);
    }

    public static void SetId(Span<byte> message, ushort id)
    {
        if (message.Length >= 2)
            WriteU16(message, 0, id);
    }

    public static void AdjustTtls(Span<byte> message, IReadOnlyList<int> offsets,
        uint elapsedSeconds, uint? overrideTtl = null)
    {
        foreach (var offset in offsets)
        {
            if (offset < 0 || offset + 4 > message.Length)
                continue;

            uint ttl;
            if (overrideTtl is { } forced)
            {
                ttl = forced;
            }
            else
            {
                var current = ReadU32(message, offset);
                ttl = current > elapsedSeconds ? current - elapsedSeconds : 0;
            }
            WriteU32(message, offset, ttl);
        }
    }

    public static bool IsValidResponse(ReadOnlySpan<byte> response, DnsQuestion question,
        ushort expectedId)
    {
        if (!TryParseMessage(response, out var layout))
            return false;
        if (!layout.IsResponse || layout.Id != expectedId)
            return false;
        return layout.Question.Type == question.Type &&
               layout.Question.Class == question.Class &&
               string.Equals(layout.Question.Name, question.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>构造单问题递归查询，用于上游探测与测试。</summary>
    public static byte[] BuildQuery(string name, ushort type, ushort id = 0,
        bool recursionDesired = true)
    {
        using var stream = new MemoryStream();
        WriteU16(stream, id);
        WriteU16(stream, recursionDesired ? (ushort)0x0100 : (ushort)0);
        WriteU16(stream, 1);
        WriteU16(stream, 0);
        WriteU16(stream, 0);
        WriteU16(stream, 0);
        var encoded = EncodeName(name);
        stream.Write(encoded, 0, encoded.Length);
        WriteU16(stream, type);
        WriteU16(stream, 1);
        return stream.ToArray();
    }

    public static byte[] BuildError(byte[] request, DnsQuestion? question, int rcode)    {
        var requestFlags = request.Length >= 4 ? ReadU16(request, 2) : (ushort)0;
        var flags = (ushort)(0x8000 | (requestFlags & 0x0100) | 0x0080 | (rcode & 0x0F));
        using var stream = new MemoryStream();
        WriteHeader(stream, ReadId(request), flags, question is null ? (ushort)0 : (ushort)1, 0, 0, 0);
        if (question is not null)
            WriteQuestion(stream, question);
        return stream.ToArray();
    }

    public static byte[] BuildTruncated(byte[] request, DnsQuestion question)
    {
        var requestFlags = ReadU16(request, 2);
        var flags = (ushort)(0x8000 | 0x0200 | (requestFlags & 0x0100) | 0x0080);
        using var stream = new MemoryStream();
        WriteHeader(stream, ReadId(request), flags, 1, 0, 0, 0);
        WriteQuestion(stream, question);
        return stream.ToArray();
    }

    public static byte[] BuildResponse(byte[] request, DnsQuestion question,
        IReadOnlyList<DnsAnswerRecord> answers, int rcode = 0, bool authoritative = false)
    {
        var requestFlags = ReadU16(request, 2);
        var flags = (ushort)(0x8000 |
                             (authoritative ? 0x0400 : 0) |
                             (requestFlags & 0x0100) |
                             0x0080 |
                             (rcode & 0x0F));
        using var stream = new MemoryStream();
        WriteHeader(stream, ReadId(request), flags, 1, (ushort)answers.Count, 0, 0);
        WriteQuestion(stream, question);

        foreach (var answer in answers)
        {
            stream.WriteByte(0xC0);
            stream.WriteByte(0x0C);
            WriteU16(stream, answer.Type);
            WriteU16(stream, 1);
            WriteU32(stream, answer.Ttl);
            WriteU16(stream, (ushort)answer.Rdata.Length);
            stream.Write(answer.Rdata, 0, answer.Rdata.Length);
        }

        return stream.ToArray();
    }

    private static ushort ReadId(byte[] request) =>
        request.Length >= 2 ? ReadU16(request, 0) : (ushort)0;

    private static void WriteHeader(Stream stream, ushort id, ushort flags,
        ushort questionCount, ushort answerCount, ushort authorityCount, ushort additionalCount)
    {
        WriteU16(stream, id);
        WriteU16(stream, flags);
        WriteU16(stream, questionCount);
        WriteU16(stream, answerCount);
        WriteU16(stream, authorityCount);
        WriteU16(stream, additionalCount);
    }

    private static void WriteQuestion(Stream stream, DnsQuestion question)
    {
        // 始终重新编码问题名：请求中的名称可能带压缩指针，直接复制字节会失效。
        var name = EncodeName(question.Name);
        stream.Write(name, 0, name.Length);
        WriteU16(stream, question.Type);
        WriteU16(stream, question.Class);
    }

    private static void WriteU16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
