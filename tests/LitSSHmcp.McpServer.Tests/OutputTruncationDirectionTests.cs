using LitSSHmcp.McpServer.Services;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>
/// 长日志场景：MCP 层截断方向必须与 Agent 层 PreferTail 一致（保留最新内容），
/// 否则 log_query(tail=500) 的最新错误会在 MCP 层先被砍掉。
/// </summary>
public class OutputTruncationDirectionTests
{
    [Fact]
    public void Truncate_head_keeps_beginning_by_default()
    {
        var text = string.Concat(Enumerable.Range(0, 100).Select(i => $"LINE-{i:D3}\n"));

        var (output, truncated, original) = ToolSupport.Truncate(text, 100, preferTail: false);

        Assert.True(truncated);
        Assert.Equal(text.Length, original);
        Assert.Contains("LINE-000", output);
        Assert.DoesNotContain("LINE-099", output);
        Assert.Contains("重新查询", output);
    }

    [Fact]
    public void Truncate_tail_keeps_latest_error_for_logs()
    {
        var text = string.Concat(Enumerable.Range(0, 100).Select(i => $"LINE-{i:D3}\n"));

        var (output, truncated, original) = ToolSupport.Truncate(text, 100, preferTail: true);

        Assert.True(truncated);
        Assert.Equal(text.Length, original);
        Assert.Contains("LINE-099", output);
        Assert.DoesNotContain("LINE-000", output);
        Assert.Contains("最新内容", output);
    }

    [Fact]
    public void Truncate_tail_does_not_split_surrogate_pair()
    {
        // 500 个 A + 一个 emoji(2 个 UTF-16 单元) + 10 字符尾巴 = 512。
        // 保尾保留 11 字符时，原生切点 501 正好落在 emoji 的低代理项上；
        // SafeCut 应前移到 500，让 emoji 完整保留而不是留下孤立低代理项。
        var text = new string('A', 500) + "\U0001F6A8" + "ERROR-TAIL";

        var (output, _, _) = ToolSupport.Truncate(text, 11, preferTail: true);

        Assert.Contains("\U0001F6A8", output);
        Assert.Contains("ERROR-TAIL", output);
    }

    [Fact]
    public void Truncate_within_budget_returns_original()
    {
        const string text = "short output";

        var (output, truncated, original) = ToolSupport.Truncate(text, 100, preferTail: true);

        Assert.False(truncated);
        Assert.Equal(text, output);
        Assert.Equal(text.Length, original);
    }
}
