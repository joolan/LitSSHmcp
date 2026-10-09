using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>粗略 token 估算（无第三方分词器）：CJK 字符约 1 token/字，其它约 4 字符/token。</summary>
public static class TokenEstimator
{
    public static int Estimate(IEnumerable<ChatMessage> messages) => messages.Sum(Estimate);

    /// <summary>估算单个工具定义（名称 + 描述 + 参数 JSON schema）的 token。</summary>
    public static int EstimateTool(AITool? tool)
    {
        if (tool is AIFunction function)
        {
            return EstimateText(function.Name)
                 + EstimateText(function.Description)
                 + EstimateText(function.JsonSchema.ToString());
        }
        return EstimateText(tool?.ToString());
    }

    /// <summary>估算全部工具定义占用的 token（前缀中的固定开销，不计入消息裁剪上限）。</summary>
    public static int EstimateToolDefinitions(IEnumerable<AITool> tools) => tools.Sum(EstimateTool);

    public static int Estimate(ChatMessage message)
    {
        var tokens = 0;
        foreach (var content in message.Contents)
        {
            tokens += content switch
            {
                TextContent text => EstimateText(text.Text),
                FunctionCallContent call => EstimateText(call.Name) + EstimateText(SerializeArgs(call.Arguments)),
                FunctionResultContent result => EstimateText(result.Result?.ToString()),
                DataContent data => EstimateData(data),
                _ => 0
            };
        }
        return tokens;
    }

    /// <summary>图像 token 的粗略基准值（无法解析尺寸时的回退）。</summary>
    public const int ImageFallbackTokens = 1100;

    private const int ImageBaseTokens = 85;
    private const int ImageTileTokens = 170;

    /// <summary>
    /// 估算 <see cref="DataContent"/> 的 token：图像按 OpenAI 分块公式（2048 见方缩放 → 最短边 768 → 每 512 块 170 + 85）
    /// 粗略估算；无法解析尺寸（如 webp）时回退 <see cref="ImageFallbackTokens"/>。非图像二进制计 0。
    /// </summary>
    public static int EstimateData(DataContent data)
    {
        if (!data.HasTopLevelMediaType("image"))
            return 0;
        return TryReadImageSize(data.Data.Span, out var width, out var height)
            ? EstimateImageTokens(width, height)
            : ImageFallbackTokens;
    }

    private static int EstimateImageTokens(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return ImageFallbackTokens;

        var scale = Math.Min(1.0, 2048.0 / Math.Max(width, height));
        var w = width * scale;
        var h = height * scale;

        var shortest = Math.Min(w, h);
        if (shortest > 768)
        {
            var s = 768.0 / shortest;
            w *= s;
            h *= s;
        }

        var tiles = Math.Max(1, (int)Math.Ceiling(w / 512.0) * (int)Math.Ceiling(h / 512.0));
        return ImageBaseTokens + ImageTileTokens * tiles;
    }

    private static bool TryReadImageSize(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;

        // PNG: IHDR 宽高为大端 uint32 @16/@20
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            width = (int)ReadUInt32Be(b, 16);
            height = (int)ReadUInt32Be(b, 20);
            return width > 0 && height > 0;
        }

        // GIF: 逻辑屏幕宽高为小端 uint16 @6/@8
        if (b.Length >= 10 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
        {
            width = b[6] | (b[7] << 8);
            height = b[8] | (b[9] << 8);
            return width > 0 && height > 0;
        }

        // BMP: 宽高为小端 int32 @18/@22
        if (b.Length >= 26 && b[0] == (byte)'B' && b[1] == (byte)'M')
        {
            width = ReadInt32Le(b, 18);
            height = Math.Abs(ReadInt32Le(b, 22));
            return width > 0 && height > 0;
        }

        // JPEG: 扫描 SOF 段
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
            return TryReadJpegSize(b, out width, out height);

        return false;
    }

    private static bool TryReadJpegSize(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8)) { i += 2; continue; }

            var len = (b[i + 2] << 8) | b[i + 3];
            if (len < 2) break;

            var isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                height = (b[i + 5] << 8) | b[i + 6];
                width = (b[i + 7] << 8) | b[i + 8];
                return width > 0 && height > 0;
            }

            i += 2 + len;
        }
        return false;
    }

    private static uint ReadUInt32Be(ReadOnlySpan<byte> b, int offset) =>
        ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) | ((uint)b[offset + 2] << 8) | b[offset + 3];

    private static int ReadInt32Le(ReadOnlySpan<byte> b, int offset) =>
        b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24);

    public static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var cjk = 0;
        var other = 0;
        foreach (var ch in text)
        {
            if (IsCjk(ch)) cjk++;
            else other++;
        }
        return cjk + (other + 3) / 4;
    }

    private static string SerializeArgs(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return string.Empty;
        try { return System.Text.Json.JsonSerializer.Serialize(arguments); }
        catch { return string.Empty; }
    }

    private static bool IsCjk(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) ||   // 基本汉字
        (ch >= 0x3400 && ch <= 0x4DBF) ||   // 扩展A
        (ch >= 0x3000 && ch <= 0x303F) ||   // CJK 标点
        (ch >= 0xFF00 && ch <= 0xFFEF);     // 全角
}
