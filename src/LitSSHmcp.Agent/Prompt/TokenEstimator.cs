using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>粗略 token 估算（无第三方分词器）：CJK 字符约 1 token/字，其它约 4 字符/token。</summary>
public static class TokenEstimator
{
    public static int Estimate(IEnumerable<ChatMessage> messages) => messages.Sum(Estimate);

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
                _ => 0
            };
        }
        return tokens;
    }

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
