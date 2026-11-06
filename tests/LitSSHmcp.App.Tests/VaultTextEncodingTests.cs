using System.Text;
using LitSSHmcp.App.Services.Toolbox;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class VaultTextEncodingTests
{
    [Fact]
    public void Utf8_with_bom_roundtrips_preserving_preamble()
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("中文文本")).ToArray();
        var payload = VaultTextEncoding.Decode(bytes);

        Assert.Equal("中文文本", payload.Text);
        Assert.Equal(3, payload.Preamble.Length);
        Assert.Equal(bytes, VaultTextEncoding.Encode(payload.Text, payload));
    }

    [Fact]
    public void Utf16_le_roundtrips()
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("hello 文本")).ToArray();
        var payload = VaultTextEncoding.Decode(bytes);

        Assert.Equal("hello 文本", payload.Text);
        Assert.Equal(bytes, VaultTextEncoding.Encode(payload.Text, payload));
    }

    [Fact]
    public void Utf8_without_bom_roundtrips()
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes("plain");
        var payload = VaultTextEncoding.Decode(bytes);

        Assert.Equal("plain", payload.Text);
        Assert.Empty(payload.Preamble);
        Assert.Equal(bytes, VaultTextEncoding.Encode(payload.Text, payload));
    }
}
