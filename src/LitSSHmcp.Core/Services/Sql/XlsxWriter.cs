using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LitSSHmcp.Core.Services.Sql;

/// <summary>
/// 生成最小可用的 Excel 工作簿（.xlsx，SpreadsheetML）。无第三方依赖：
/// 打包必需的 OOXML 部件，数值写成数字单元格、其余写成内联字符串。
/// </summary>
public static class XlsxWriter
{
    public static void Write(Stream stream, IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows, string sheetName = "Sheet1")
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "[Content_Types].xml", ContentTypes);
        Add(zip, "_rels/.rels", RootRels);
        Add(zip, "xl/workbook.xml", Workbook(SanitizeSheetName(sheetName)));
        Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
        Add(zip, "xl/worksheets/sheet1.xml", Sheet(columns, rows));
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Sheet(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

        // 表头
        sb.Append("<row r=\"1\">");
        for (var c = 0; c < columns.Count; c++)
            sb.Append(StringCell(c, 1, columns[c]));
        sb.Append("</row>");

        // 数据行
        var rowIndex = 2;
        foreach (var row in rows)
        {
            sb.Append("<row r=\"").Append(rowIndex).Append("\">");
            for (var c = 0; c < columns.Count && c < row.Count; c++)
                sb.Append(ValueCell(c, rowIndex, row[c]));
            sb.Append("</row>");
            rowIndex++;
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string ValueCell(int col, int row, object? value)
    {
        if (value is null || value is DBNull)
            return string.Empty;
        if (value is bool b)
            return $"<c r=\"{CellRef(col, row)}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>";
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            var num = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
            return $"<c r=\"{CellRef(col, row)}\"><v>{num}</v></c>";
        }
        return StringCell(col, row, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static string StringCell(int col, int row, string text)
        => $"<c r=\"{CellRef(col, row)}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(text)}</t></is></c>";

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'':
                    sb.Append("&#39;");
                    break;
                default:
                    // 去掉 OOXML 不允许的控制字符
                    if (ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20)
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string CellRef(int col, int row) => ColumnName(col) + row.ToString(CultureInfo.InvariantCulture);

    private static string ColumnName(int col)
    {
        var name = string.Empty;
        col += 1;
        while (col > 0)
        {
            var rem = (col - 1) % 26;
            name = (char)('A' + rem) + name;
            col = (col - 1) / 26;
        }
        return name;
    }

    private static string SanitizeSheetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Sheet1";
        foreach (var c in new[] { '[', ']', ':', '*', '?', '/', '\\' })
            name = name.Replace(c, '_');
        return name.Length > 31 ? name[..31] : name;
    }

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "</Types>";

    private const string RootRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private static string Workbook(string sheetName)
        => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
           "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
           "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
           "<sheets><sheet name=\"" + Escape(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

    private const string WorkbookRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "</Relationships>";
}
