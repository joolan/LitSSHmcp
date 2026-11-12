using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;

namespace LitSSHmcp.App.Services.Toolbox;

public enum TextCodecMode
{
    JsonFormat,
    JsonCompact,
    XmlFormat,
    Base64Encode,
    Base64Decode,
    UrlEncode,
    UrlDecode,
    HtmlEncode,
    HtmlDecode,
    JwtDecode,
    Timestamp,
    Sha256,
    Sha512,
    Md5,
    Uuid
}

public sealed record TextCodecChoice(TextCodecMode Mode, string Title, string Hint);

public static class TextCodecService
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyList<TextCodecChoice> Modes { get; } = new[]
    {
        new TextCodecChoice(TextCodecMode.JsonFormat, "JSON 格式化", "解析并缩进 JSON"),
        new TextCodecChoice(TextCodecMode.JsonCompact, "JSON 压缩", "移除 JSON 缩进与多余空白"),
        new TextCodecChoice(TextCodecMode.XmlFormat, "XML 格式化", "解析并缩进 XML"),
        new TextCodecChoice(TextCodecMode.Base64Encode, "Base64 编码", "UTF-8 文本 → Base64"),
        new TextCodecChoice(TextCodecMode.Base64Decode, "Base64 解码", "Base64 → UTF-8 文本"),
        new TextCodecChoice(TextCodecMode.UrlEncode, "URL 编码", "转义 URL 中的特殊字符"),
        new TextCodecChoice(TextCodecMode.UrlDecode, "URL 解码", "还原 URL 转义字符"),
        new TextCodecChoice(TextCodecMode.HtmlEncode, "HTML 编码", "转义 HTML 特殊字符"),
        new TextCodecChoice(TextCodecMode.HtmlDecode, "HTML 解码", "还原 HTML 实体"),
        new TextCodecChoice(TextCodecMode.JwtDecode, "JWT 解码", "解码 header / payload（不校验签名）"),
        new TextCodecChoice(TextCodecMode.Timestamp, "时间戳转换", "Unix 秒/毫秒与本地/UTC 时间互转"),
        new TextCodecChoice(TextCodecMode.Sha256, "SHA-256", "UTF-8 文本哈希（十六进制）"),
        new TextCodecChoice(TextCodecMode.Sha512, "SHA-512", "UTF-8 文本哈希（十六进制）"),
        new TextCodecChoice(TextCodecMode.Md5, "MD5", "UTF-8 文本哈希（仅兼容用途）"),
        new TextCodecChoice(TextCodecMode.Uuid, "UUID", "生成一个新的随机 UUID")
    };

    public static string Transform(TextCodecMode mode, string input)
    {
        input ??= string.Empty;
        return mode switch
        {
            TextCodecMode.JsonFormat => FormatJson(input, indented: true),
            TextCodecMode.JsonCompact => FormatJson(input, indented: false),
            TextCodecMode.XmlFormat => FormatXml(input),
            TextCodecMode.Base64Encode => Convert.ToBase64String(Encoding.UTF8.GetBytes(input)),
            TextCodecMode.Base64Decode => Encoding.UTF8.GetString(Convert.FromBase64String(CleanBase64(input))),
            TextCodecMode.UrlEncode => Uri.EscapeDataString(input),
            TextCodecMode.UrlDecode => Uri.UnescapeDataString(input),
            TextCodecMode.HtmlEncode => WebUtility.HtmlEncode(input),
            TextCodecMode.HtmlDecode => WebUtility.HtmlDecode(input),
            TextCodecMode.JwtDecode => DecodeJwt(input),
            TextCodecMode.Timestamp => ConvertTimestamp(input),
            TextCodecMode.Sha256 => Hash(input, SHA256.Create()),
            TextCodecMode.Sha512 => Hash(input, SHA512.Create()),
            TextCodecMode.Md5 => Hash(input, MD5.Create()),
            TextCodecMode.Uuid => Guid.NewGuid().ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    public static (int Characters, int Lines, int Utf8Bytes) Measure(string? input)
    {
        var text = input ?? string.Empty;
        var lines = text.Length == 0 ? 0 : text.Count(ch => ch == '\n') + 1;
        return (text.Length, lines, Encoding.UTF8.GetByteCount(text));
    }

    private static string FormatJson(string input, bool indented)
    {
        using var document = JsonDocument.Parse(input);
        return JsonSerializer.Serialize(
            document.RootElement,
            indented ? PrettyJsonOptions : CompactJsonOptions);
    }

    private static string FormatXml(string input)
        => XDocument.Parse(input).ToString();

    private static string CleanBase64(string input)
        => string.Concat(input.Where(ch => !char.IsWhiteSpace(ch)));

    private static string DecodeJwt(string input)
    {
        var parts = input.Trim().Split('.');
        if (parts.Length < 2)
            throw new FormatException("JWT 至少应包含 header 和 payload 两段。");

        var header = FormatJson(Base64UrlDecode(parts[0]), indented: true);
        var payload = FormatJson(Base64UrlDecode(parts[1]), indented: true);
        var builder = new StringBuilder();
        builder.AppendLine("Header:");
        builder.AppendLine(header);
        builder.AppendLine();
        builder.AppendLine("Payload:");
        builder.AppendLine(payload);
        if (parts.Length >= 3)
        {
            builder.AppendLine();
            builder.AppendLine("Signature:");
            builder.AppendLine(parts[2]);
        }
        return builder.ToString().TrimEnd();
    }

    private static string Base64UrlDecode(string input)
    {
        var value = input.Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    private static string ConvertTimestamp(string input)
    {
        var text = input.Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            DateTimeOffset value;
            try
            {
                value = Math.Abs(unix) >= 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new FormatException("时间戳超出可转换范围。");
            }
            return FormatTimestamp(value);
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var parsed))
            return FormatTimestamp(parsed);

        throw new FormatException("请输入 Unix 秒/毫秒时间戳，或可解析的日期时间字符串。");
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        $"Unix 秒：{value.ToUnixTimeSeconds()}{Environment.NewLine}" +
        $"Unix 毫秒：{value.ToUnixTimeMilliseconds()}{Environment.NewLine}" +
        $"UTC：{value.UtcDateTime:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
        $"本地：{value.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}";

    private static string Hash(string input, HashAlgorithm algorithm)
    {
        using (algorithm)
            return Convert.ToHexString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
