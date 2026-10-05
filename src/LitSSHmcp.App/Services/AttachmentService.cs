using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
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
            return new ParsedAttachment(name, AttachmentKind.Image, MediaTypeOf(ext), bytes, string.Empty);
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
