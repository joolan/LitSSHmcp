using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ZXing;
using ZXing.Common;

namespace LitSSHmcp.App.Services.Toolbox;

public static class QrCodeReader
{
    public static bool TryReadOtpauth(string imagePath, out string? uri,
        out PasswordTotpConfig? config, out string? error)
    {
        uri = null;
        config = null;
        error = null;
        try
        {
            using var bitmap = new Bitmap(imagePath);
            var width = bitmap.Width;
            var height = bitmap.Height;
            if (width <= 0 || height <= 0)
            {
                error = "图片尺寸无效。";
                return false;
            }

            var pixels = new byte[width * height * 4];
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < height; y++)
                {
                    var row = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(row, pixels, y * width * 4, width * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return TryDecodeOtpauth(pixels, width, height, out uri, out config, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryDecodeOtpauth(byte[] bgraPixels, int width, int height,
        out string? uri, out PasswordTotpConfig? config, out string? error)
    {
        uri = null;
        config = null;
        error = null;
        try
        {
            var source = new RGBLuminanceSource(bgraPixels, width, height,
                RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
                }
            };
            var result = reader.Decode(source);
            if (result?.Text is not { Length: > 0 } text)
            {
                error = "未识别到二维码。";
                return false;
            }
            if (!TotpGenerator.TryParseUri(text, out config))
            {
                error = "二维码内容不是有效的 otpauth:// TOTP 链接。";
                return false;
            }

            uri = text;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
