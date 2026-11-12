using LitSSHmcp.App.Services.Toolbox;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class CertificateTargetParserTests
{
    [Theory]
    [InlineData("https://example.com", "example.com", 443)]
    [InlineData("https://example.com:8443/path?q=1", "example.com", 8443)]
    [InlineData("example.com", "example.com", 443)]
    [InlineData("example.com:8443", "example.com", 8443)]
    [InlineData("wss://example.com/socket", "example.com", 443)]
    public void Parse_accepts_urls_and_bare_hosts(string input, string host, int port)
    {
        Assert.True(CertificateTargetParser.TryParse(input, out var target, out var error), error);
        Assert.NotNull(target);
        Assert.Equal(host, target!.Host);
        Assert.Equal(port, target.Port);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("")]
    public void Parse_rejects_non_tls_targets(string input)
    {
        Assert.False(CertificateTargetParser.TryParse(input, out _, out _));
    }

    [Fact]
    public void Split_deduplicates_and_ignores_comments_blank_lines_and_csv_headers()
    {
        var values = CertificateTargetParser.SplitInput(
            "https://a.example:443\r\n\r\n# comment\r\nhttps://a.example:443\r\nb.example:8443\nurl,cert\n");

        Assert.Equal(new[] { "https://a.example:443", "b.example:8443", "url" }, values);
    }

    [Fact]
    public void Csv_row_escapes_commas_and_quotes()
    {
        var result = new CertificateCheckResult
        {
            Input = "https://example.com/a,b",
            Host = "example.com",
            Port = 443,
            StatusText = "正常",
            Severity = "Ok",
            Subject = "CN=\"example\", O=Test"
        };

        var row = result.ToCsvRow();
        Assert.Contains("\"https://example.com/a,b\"", row);
        Assert.Contains("\"CN=\"\"example\"\", O=Test\"", row);
    }
}
