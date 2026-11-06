using LitSSHmcp.App.Services.Toolbox;
using Xunit;
using ZXing;
using ZXing.Common;

namespace LitSSHmcp.App.Tests;

public class QrCodeReaderTests
{
    [Fact]
    public void Decodes_otpauth_qr_pixels()
    {
        const string uri = "otpauth://totp/GitHub:user%40example.com?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=GitHub&algorithm=SHA256&digits=8&period=60";
        const int size = 240;
        var matrix = new MultiFormatWriter().encode(uri, BarcodeFormat.QR_CODE, size, size);
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var value = matrix[x, y] ? (byte)0 : (byte)255;
                var offset = (y * size + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        Assert.True(QrCodeReader.TryDecodeOtpauth(pixels, size, size,
            out var decodedUri, out var config, out var error), error);
        Assert.Equal(uri, decodedUri);
        Assert.NotNull(config);
        Assert.Equal("GitHub", config.Issuer);
        Assert.Equal("user@example.com", config.Account);
        Assert.Equal("SHA256", config.Algorithm);
        Assert.Equal(8, config.Digits);
        Assert.Equal(60, config.Period);
    }
}
