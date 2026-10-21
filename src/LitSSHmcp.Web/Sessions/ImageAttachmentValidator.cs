using System.Buffers.Binary;

namespace LitSSHmcp.Web.Sessions;

/// <summary>校验通过后的图片元数据（格式由文件头识别，不信任客户端声明的 MIME/扩展名）。</summary>
public sealed record ImageAttachmentInfo(string Extension, string ContentType, int Width, int Height);

/// <summary>
/// 聊天图片附件的边界校验：只接受 PNG/JPEG/GIF/WebP，按 magic bytes 识别格式，
/// 从文件头解析像素尺寸并拒绝超限图片，避免“改扩展名/伪造 MIME”和压缩炸弹。
/// 校验只解析头部，不做完整解码。
/// </summary>
public static class ImageAttachmentValidator
{
    public const long MaxBytes = 10L * 1024 * 1024;
    public const int MaxDimension = 8192;
    public const long MaxPixels = 40_000_000;

    public static ImageAttachmentInfo Validate(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            throw new InvalidOperationException("图片为空");

        if (data.Length > MaxBytes)
            throw new InvalidOperationException("图片过大（单张上限 10MB）");

        var info = TryReadPng(data)
                   ?? TryReadJpeg(data)
                   ?? TryReadGif(data)
                   ?? TryReadWebp(data)
                   ?? throw new InvalidOperationException("仅支持有效的 png/jpeg/webp/gif 图片");

        if (info.Width <= 0 || info.Height <= 0)
            throw new InvalidOperationException("图片尺寸无效");

        if (info.Width > MaxDimension || info.Height > MaxDimension
            || (long)info.Width * info.Height > MaxPixels)
        {
            throw new InvalidOperationException(
                $"图片尺寸过大（最大 {MaxDimension}×{MaxDimension}，且总像素不超过 {MaxPixels}）");
        }

        return info;
    }

    /// <summary>用于聊天标记/接口展示的安全文件名：去路径、控制字符，限长，空名回退 image+扩展名。</summary>
    public static string SanitizeDisplayName(string? name, string extension)
    {
        var leaf = (name ?? string.Empty).Replace('\\', '/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0)
            leaf = leaf[(slash + 1)..];

        var cleaned = new string(leaf.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length > 120)
            cleaned = cleaned[..120];
        if (cleaned.Length == 0)
            cleaned = "image" + (extension.StartsWith('.') ? extension : "." + extension);
        return cleaned;
    }

    private static ImageAttachmentInfo? TryReadPng(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 24 || !data[..8].SequenceEqual(signature))
            return null;
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidOperationException("PNG 头无效");
        if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4)) != 13)
            throw new InvalidOperationException("PNG IHDR 长度无效");

        var width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        if (width > int.MaxValue || height > int.MaxValue)
            throw new InvalidOperationException("PNG 尺寸无效");

        return new ImageAttachmentInfo(".png", "image/png", (int)width, (int)height);
    }

    private static ImageAttachmentInfo? TryReadJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return null;

        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset < data.Length && data[offset] == 0xFF)
                offset++;
            if (offset >= data.Length)
                break;

            var marker = data[offset++];
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (marker == 0xD9 || marker == 0xDA)
                break;

            if (offset + 2 > data.Length)
                break;
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > data.Length)
                throw new InvalidOperationException("JPEG 段长度无效");

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (segmentLength < 7)
                    throw new InvalidOperationException("JPEG 尺寸段无效");
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                return new ImageAttachmentInfo(".jpg", "image/jpeg", width, height);
            }

            offset += segmentLength;
        }

        throw new InvalidOperationException("JPEG 缺少尺寸信息");
    }

    private static ImageAttachmentInfo? TryReadGif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
            return null;
        if (!data[..6].SequenceEqual("GIF87a"u8) && !data[..6].SequenceEqual("GIF89a"u8))
            return null;

        var width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
        return new ImageAttachmentInfo(".gif", "image/gif", width, height);
    }

    private static ImageAttachmentInfo? TryReadWebp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20
            || !data[..4].SequenceEqual("RIFF"u8)
            || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return null;
        }

        var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4));
        if (chunkSize > int.MaxValue || 20L + chunkSize > data.Length)
            throw new InvalidOperationException("WebP 块长度无效");

        if (data.Slice(12, 4).SequenceEqual("VP8X"u8))
        {
            if (chunkSize < 10 || data.Length < 30)
                throw new InvalidOperationException("WebP VP8X 头无效");
            var width = 1 + ReadUInt24LittleEndian(data.Slice(24, 3));
            var height = 1 + ReadUInt24LittleEndian(data.Slice(27, 3));
            return new ImageAttachmentInfo(".webp", "image/webp", width, height);
        }

        if (data.Slice(12, 4).SequenceEqual("VP8 "u8))
        {
            if (chunkSize < 10 || data.Length < 30
                || data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A)
            {
                throw new InvalidOperationException("WebP VP8 头无效");
            }
            var width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26, 2)) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2)) & 0x3FFF;
            return new ImageAttachmentInfo(".webp", "image/webp", width, height);
        }

        if (data.Slice(12, 4).SequenceEqual("VP8L"u8))
        {
            if (chunkSize < 5 || data.Length < 25 || data[20] != 0x2F)
                throw new InvalidOperationException("WebP VP8L 头无效");
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(21, 4));
            var width = (int)(bits & 0x3FFF) + 1;
            var height = (int)((bits >> 14) & 0x3FFF) + 1;
            return new ImageAttachmentInfo(".webp", "image/webp", width, height);
        }

        throw new InvalidOperationException("不支持的 WebP 编码");
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> data)
        => data[0] | (data[1] << 8) | (data[2] << 16);
}
