using System.Data;
using System.IO;
using System.Text.Json;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class WorkbenchExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-export-" + Guid.NewGuid().ToString("N"));

    public WorkbenchExporterTests() => Directory.CreateDirectory(_root);

    private static DataView BuildView()
    {
        var table = new DataTable("结果");
        table.Columns.Add("name", typeof(object));
        table.Columns.Add("note", typeof(object));
        table.Rows.Add("a,b", "say \"hi\"");
        table.Rows.Add("plain", null);
        return table.DefaultView;
    }

    [Fact]
    public void Csv_escapes_commas_quotes_and_null()
    {
        var path = Path.Combine(_root, "out.csv");
        Assert.Equal(2, WorkbenchExporter.WriteCsv(BuildView(), path));
        var text = File.ReadAllText(path);
        Assert.Contains("\"a,b\"", text);
        Assert.Contains("\"say \"\"hi\"\"\"", text);
        Assert.Contains("plain,", text);
    }

    [Fact]
    public void Json_exports_null_and_values()
    {
        var path = Path.Combine(_root, "out.json");
        Assert.Equal(2, WorkbenchExporter.WriteJson(BuildView(), path));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rows = doc.RootElement;
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("a,b", rows[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("note").ValueKind);
    }

    [Fact]
    public void Excel_writes_workbook()
    {
        var path = Path.Combine(_root, "out.xlsx");
        Assert.Equal(2, WorkbenchExporter.WriteExcel(BuildView(), path));
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    public void Escape_csv_matches_rfc4180(string value, string expected) =>
        Assert.Equal(expected, WorkbenchExporter.EscapeCsv(value));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
