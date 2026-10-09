using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class TokenEstimatorTests
{
    [Theory]
    [InlineData("你好吗", 3)]      // 3 个 CJK 字符
    [InlineData("abcd", 1)]       // 4 个 ASCII → 1
    [InlineData("", 0)]
    public void EstimateText_handles_cjk_and_ascii(string text, int expected)
    {
        Assert.Equal(expected, TokenEstimator.EstimateText(text));
    }

    [Fact]
    public void Estimate_messages_sums_content()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "你好"),        // 2
            new(ChatRole.Assistant, "abcd")    // 1
        };
        Assert.Equal(3, TokenEstimator.Estimate(messages));
    }

    [Fact]
    public void EstimateData_small_png_uses_single_tile()
    {
        Assert.Equal(255, TokenEstimator.EstimateData(new DataContent(PngBytes(512, 512), "image/png")));
    }

    [Fact]
    public void EstimateData_png_uses_tile_formula()
    {
        // 1920x1080 → 最短边缩到 768 → 1366x768 → 3x2 块 → 85 + 170*6
        Assert.Equal(1105, TokenEstimator.EstimateData(new DataContent(PngBytes(1920, 1080), "image/png")));
    }

    [Fact]
    public void EstimateData_reads_jpeg_dimensions()
    {
        Assert.Equal(1105, TokenEstimator.EstimateData(new DataContent(JpegBytes(1920, 1080), "image/jpeg")));
    }

    [Fact]
    public void EstimateData_falls_back_for_unknown_image_format()
    {
        var content = new DataContent(new byte[] { 1, 2, 3, 4 }, "image/webp");
        Assert.Equal(TokenEstimator.ImageFallbackTokens, TokenEstimator.EstimateData(content));
    }

    [Fact]
    public void EstimateData_non_image_is_zero()
    {
        Assert.Equal(0, TokenEstimator.EstimateData(new DataContent(new byte[] { 1, 2, 3 }, "application/pdf")));
    }

    [Fact]
    public void EstimateTool_includes_name_description_and_schema()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "my_tool", "这是一个测试工具描述");
        var tokens = TokenEstimator.EstimateTool(tool);
        Assert.True(tokens > TokenEstimator.EstimateText("my_tool"));
        Assert.True(TokenEstimator.EstimateToolDefinitions(new[] { tool }) >= tokens);
    }

    private static byte[] PngBytes(int width, int height)
    {
        var b = new byte[24];
        b[0] = 0x89; b[1] = 0x50; b[2] = 0x4E; b[3] = 0x47;
        b[4] = 0x0D; b[5] = 0x0A; b[6] = 0x1A; b[7] = 0x0A;
        WriteBigEndian(b, 16, (uint)width);
        WriteBigEndian(b, 20, (uint)height);
        return b;
    }

    private static byte[] JpegBytes(int width, int height)
    {
        var b = new byte[12];
        b[0] = 0xFF; b[1] = 0xD8;             // SOI
        b[2] = 0xFF; b[3] = 0xC0;             // SOF0
        b[4] = 0x00; b[5] = 0x11;             // 段长
        b[6] = 0x08;                          // 精度
        b[7] = (byte)(height >> 8); b[8] = (byte)height;
        b[9] = (byte)(width >> 8); b[10] = (byte)width;
        return b;
    }

    private static void WriteBigEndian(byte[] b, int offset, uint value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }
}
