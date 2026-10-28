using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class MemoryRecallQueryBuilderTests
{
    [Fact]
    public void Short_follow_up_includes_recent_context_and_assets()
    {
        var query = MemoryRecallQueryBuilder.Build(
            "那台机器呢",
            new[] { "检查 ssh:web01 的 nginx 502" },
            new[] { "ssh:web01" });

        Assert.Contains("ssh:web01", query);
        Assert.Contains("nginx 502", query);
        Assert.Contains("那台机器呢", query);
    }

    [Fact]
    public void Self_contained_question_does_not_pull_unrelated_context()
    {
        var query = MemoryRecallQueryBuilder.Build(
            "nginx 502 怎么排查",
            new[] { "磁盘满了怎么清理" },
            null);

        Assert.Equal("nginx 502 怎么排查", query);
        Assert.DoesNotContain("磁盘", query);
    }

    [Fact]
    public void Query_is_capped_without_losing_current_text()
    {
        var query = MemoryRecallQueryBuilder.Build(
            "继续排查这个问题",
            new[] { new string('上', 500) },
            new[] { "ssh:web01" },
            maxChars: 80);

        Assert.True(query.Length <= 80, $"query length {query.Length}");
        Assert.Contains("继续排查这个问题", query);
    }
}
