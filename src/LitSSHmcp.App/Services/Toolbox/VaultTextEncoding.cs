using System.Text;

namespace LitSSHmcp.App.Services.Toolbox;

public sealed record VaultTextPayload(string Text, Encoding Encoding, byte[] Preamble);

/// <summary>文本文件 BOM / UTF-8 识别。第一版不支持无 BOM 的本地 ANSI 代码页。</summary>
public static class VaultTextEncoding
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static VaultTextPayload Decode(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            return DecodeWith(bytes, 4, new UTF32Encoding(bigEndian: false, byteOrderMark: true));
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return DecodeWith(bytes, 4, new UTF32Encoding(bigEndian: true, byteOrderMark: true));
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return DecodeWith(bytes, 3, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return DecodeWith(bytes, 2, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return DecodeWith(bytes, 2, new UnicodeEncoding(bigEndian: true, byteOrderMark: true));

        try
        {
            var text = Utf8NoBom.GetString(bytes);
            return new VaultTextPayload(text, Utf8NoBom, Array.Empty<byte>());
        }
        catch
        {
            return new VaultTextPayload(Encoding.Default.GetString(bytes), Encoding.Default, Array.Empty<byte>());
        }
    }

    public static byte[] Encode(string text, VaultTextPayload payload)
    {
        var body = payload.Encoding.GetBytes(text);
        if (payload.Preamble.Length == 0)
            return body;
        var result = new byte[payload.Preamble.Length + body.Length];
        Buffer.BlockCopy(payload.Preamble, 0, result, 0, payload.Preamble.Length);
        Buffer.BlockCopy(body, 0, result, payload.Preamble.Length, body.Length);
        return result;
    }

    private static VaultTextPayload DecodeWith(byte[] bytes, int preambleLength, Encoding encoding)
    {
        var preamble = bytes.AsSpan(0, preambleLength).ToArray();
        return new VaultTextPayload(encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength),
            encoding, preamble);
    }
}
