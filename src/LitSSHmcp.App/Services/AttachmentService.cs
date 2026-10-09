using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UglyToad.PdfPig;

namespace LitSSHmcp.App.Services;

public enum AttachmentKind
{
    Image,
    Document
}

/// <summary>解析后的附件：图片给字节+媒体类型；文档给抽取出的文本。</summary>
public sealed record ParsedAttachment(string Name, AttachmentKind Kind, string? MediaType, byte[]? ImageBytes, string Text);

/// <summary>附件解析：图片按二进制；文档客户端抽取为文本（txt/md/log/json/yaml/xml/csv/代码、docx、pdf）。</summary>
public static class AttachmentService
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".json", ".yaml", ".yml", ".xml", ".csv", ".tsv",
        ".ini", ".conf", ".config", ".properties", ".env", ".sql", ".sh", ".bash", ".ps1", ".bat", ".cmd",
        ".cs", ".java", ".py", ".js", ".ts", ".go", ".rb", ".php", ".c", ".cpp", ".h", ".hpp", ".kt", ".rs",
        ".html", ".htm", ".css", ".toml"
    };

    public static bool IsImage(string path) => ImageExts.Contains(Path.GetExtension(path));

    public static ParsedAttachment Parse(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ImageExts.Contains(ext))
        {
            var bytes = File.ReadAllBytes(path);
            var (outBytes, outMedia) = DownscaleImage(bytes, MediaTypeOf(ext));
            return new ParsedAttachment(name, AttachmentKind.Image, outMedia, outBytes, string.Empty);
        }

        if (TextExts.Contains(ext))
            return new ParsedAttachment(name, AttachmentKind.Document, null, null, ReadText(path));

        if (ext == ".docx")
            return new ParsedAttachment(name, AttachmentKind.Document, null, null, ReadDocx(path));

        if (ext == ".pdf")
            return new ParsedAttachment(name, AttachmentKind.Document, null, null, ReadPdf(path));

        return new ParsedAttachment(name, AttachmentKind.Document, null, null,
            $"(暂不支持解析的文件类型 {ext}；可将其放到服务器后用工具分析，或转为文本再附上)");
    }

    private const int MaxImageDimension = 1536;
    private const int JpegQuality = 85;

    /// <summary>
    /// 发送前压缩图片：最长边 &gt; <see cref="MaxImageDimension"/> 时等比缩放；JPEG 源转 JPEG(q85)，其余转 PNG。
    /// 任何解码/编码异常或压缩后反而更大时，原样返回，保证不劣化。
    /// </summary>
    private static (byte[] Bytes, string MediaType) DownscaleImage(byte[] bytes, string mediaType)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return (bytes, mediaType);

            var frame = decoder.Frames[0];
            var longSide = Math.Max(frame.PixelWidth, frame.PixelHeight);

            // 尺寸不大且体积可控：原样返回，避免无谓重编码。
            if (longSide <= MaxImageDimension && bytes.Length <= 1_500_000)
                return (bytes, mediaType);

            BitmapSource output = frame;
            if (longSide > MaxImageDimension)
            {
                var scale = (double)MaxImageDimension / longSide;
                output = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }
            if (output.CanFreeze)
                output.Freeze();

            var useJpeg = string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase);
            BitmapEncoder encoder = useJpeg
                ? new JpegBitmapEncoder { QualityLevel = JpegQuality }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(output));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            var result = ms.ToArray();

            // 仅在确实变小时采用，避免 PNG 重编码把 JPEG 撑大。
            return result.Length < bytes.Length
                ? (result, useJpeg ? "image/jpeg" : "image/png")
                : (bytes, mediaType);
        }
        catch
        {
            return (bytes, mediaType);
        }
    }

    private static string MediaTypeOf(string ext) => ext switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    private static string ReadText(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ReadDocx(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null)
                return "(docx 解析失败: 未找到 word/document.xml)";

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var xml = reader.ReadToEnd();
            xml = xml.Replace("</w:p>", "\n").Replace("<w:br/>", "\n");
            var text = Regex.Replace(xml, "<[^>]+>", string.Empty);
            return System.Net.WebUtility.HtmlDecode(text).Trim();
        }
        catch (Exception ex)
        {
            return $"(docx 解析失败: {ex.Message})";
        }
    }

    private static string ReadPdf(string path)
    {
        try
        {
            var sb = new StringBuilder();
            using var document = PdfDocument.Open(path);
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
                sb.AppendLine();
            }
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            return $"(pdf 解析失败: {ex.Message})";
        }
    }
}
