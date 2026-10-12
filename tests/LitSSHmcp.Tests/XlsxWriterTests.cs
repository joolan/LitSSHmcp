using System.IO;
using System.IO.Compression;
using LitSSHmcp.Core.Services.Sql;
using Xunit;

namespace LitSSHmcp.Tests;

public class XlsxWriterTests
{
    [Fact]
    public void Writes_valid_xlsx_package_with_values()
    {
        using var ms = new MemoryStream();
        XlsxWriter.Write(ms, new[] { "id", "name" }, new[]
        {
            new object?[] { 1, "alice" },
            new object?[] { 2, null }
        }, "结果");

        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.Contains(zip.Entries, e => e.FullName == "[Content_Types].xml");
        Assert.Contains(zip.Entries, e => e.FullName == "xl/workbook.xml");
        Assert.Contains(zip.Entries, e => e.FullName == "xl/worksheets/sheet1.xml");

        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = reader.ReadToEnd();
        Assert.Contains("id", sheet);
        Assert.Contains("alice", sheet);
        Assert.Contains("<v>1</v>", sheet);                 // 数值写成数字单元格
        Assert.Contains("t=\"inlineStr\"", sheet);          // 文本写成内联字符串
    }
}
