using LitSSHmcp.Web.Sessions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

public class ImageAttachmentValidatorTests
{
    [Fact]
    public void Accepts_png_and_reports_detected_metadata()
    {
        var info = ImageAttachmentValidator.Validate(TestImages.Png(640, 480));

        Assert.Equal(".png", info.Extension);
        Assert.Equal("image/png", info.ContentType);
        Assert.Equal(640, info.Width);
        Assert.Equal(480, info.Height);
    }

    [Fact]
    public void Accepts_jpeg_gif_and_webp_headers()
    {
        Assert.Equal("image/jpeg", ImageAttachmentValidator.Validate(TestImages.Jpeg()).ContentType);
        Assert.Equal("image/gif", ImageAttachmentValidator.Validate(TestImages.Gif()).ContentType);
        Assert.Equal("image/webp", ImageAttachmentValidator.Validate(TestImages.WebpVp8L()).ContentType);
        Assert.Equal("image/webp", ImageAttachmentValidator.Validate(TestImages.WebpVp8X()).ContentType);
    }

    [Fact]
    public void Rejects_non_image_bytes_even_with_image_extension_or_mime()
    {
        Assert.Throws<InvalidOperationException>(
            () => ImageAttachmentValidator.Validate(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        Assert.Throws<InvalidOperationException>(
            () => ImageAttachmentValidator.Validate("MZ executable"u8));
    }

    [Theory]
    [InlineData(8193, 1)]
    [InlineData(1, 8193)]
    [InlineData(8000, 8000)]
    public void Rejects_oversized_dimensions_and_pixel_bombs(int width, int height)
    {
        Assert.Throws<InvalidOperationException>(
            () => ImageAttachmentValidator.Validate(TestImages.Png(width, height)));
    }

    [Fact]
    public void Rejects_files_over_ten_megabytes()
    {
        var oversized = new byte[ImageAttachmentValidator.MaxBytes + 1];
        TestImages.Png().CopyTo(oversized, 0);

        Assert.Throws<InvalidOperationException>(() => ImageAttachmentValidator.Validate(oversized));
    }

    [Fact]
    public void Sanitizes_display_name()
    {
        Assert.Equal("b.png", ImageAttachmentValidator.SanitizeDisplayName("C:\\temp\\a\\\r\nb.png", ".png"));
        Assert.Equal("image.png", ImageAttachmentValidator.SanitizeDisplayName(" ", ".png"));
    }
}
