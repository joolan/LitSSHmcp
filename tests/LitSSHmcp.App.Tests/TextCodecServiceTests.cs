using System.Text;
using LitSSHmcp.App.Services.Toolbox;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TextCodecServiceTests
{
    [Fact]
    public void Json_format_and_compact()
    {
        var pretty = TextCodecService.Transform(TextCodecMode.JsonFormat, "{\"b\":2,\"a\":[1]}");
        Assert.Contains(Environment.NewLine, pretty);
        Assert.Contains("\"a\": [", pretty);

        var compact = TextCodecService.Transform(TextCodecMode.JsonCompact, pretty);
        Assert.Equal("{\"b\":2,\"a\":[1]}", compact);
    }

    [Fact]
    public void Base64_round_trip_utf8()
    {
        var encoded = TextCodecService.Transform(TextCodecMode.Base64Encode, "你好");
        Assert.Equal("5L2g5aW9", encoded);
        Assert.Equal("你好", TextCodecService.Transform(TextCodecMode.Base64Decode, encoded));
    }

    [Fact]
    public void Url_and_html_round_trip()
    {
        Assert.Equal("a%20b%26c", TextCodecService.Transform(TextCodecMode.UrlEncode, "a b&c"));
        Assert.Equal("a b&c", TextCodecService.Transform(TextCodecMode.UrlDecode, "a%20b%26c"));

        var html = TextCodecService.Transform(TextCodecMode.HtmlEncode, "<a&b>");
        Assert.Equal("&lt;a&amp;b&gt;", html);
        Assert.Equal("<a&b>", TextCodecService.Transform(TextCodecMode.HtmlDecode, html));
    }

    [Fact]
    public void Jwt_decode_shows_header_and_payload()
    {
        var jwt = Base64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")
                  + "." + Base64Url("{\"sub\":\"42\",\"name\":\"运维\"}") + ".signature";

        var output = TextCodecService.Transform(TextCodecMode.JwtDecode, jwt);
        Assert.Contains("\"alg\": \"HS256\"", output);
        Assert.Contains("\"sub\": \"42\"", output);
        Assert.Contains("Signature:", output);
    }

    [Fact]
    public void Timestamp_and_hash()
    {
        var timestamp = TextCodecService.Transform(TextCodecMode.Timestamp, "0");
        Assert.Contains("Unix 秒：0", timestamp);
        Assert.Contains("1970-01-01 00:00:00", timestamp);

        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            TextCodecService.Transform(TextCodecMode.Sha256, "abc"));
    }

    [Fact]
    public void Xml_format_and_uuid()
    {
        var xml = TextCodecService.Transform(TextCodecMode.XmlFormat, "<root><item>1</item></root>");
        Assert.Contains(Environment.NewLine, xml);

        Assert.True(Guid.TryParse(TextCodecService.Transform(TextCodecMode.Uuid, string.Empty), out _));
    }

    [Fact]
    public void Measure_counts_lines_and_utf8_bytes()
    {
        var stats = TextCodecService.Measure("a\n你好");
        Assert.Equal(4, stats.Characters);
        Assert.Equal(2, stats.Lines);
        Assert.Equal(8, stats.Utf8Bytes);
    }

    private static string Base64Url(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
