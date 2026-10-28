using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class DocumentChunkerTests
{
    [Fact]
    public void Short_text_returns_single_trimmed_chunk()
    {
        var chunks = DocumentChunker.Chunk("  磁盘满怎么办  ");
        var chunk = Assert.Single(chunks);
        Assert.Equal("磁盘满怎么办", chunk);
    }

    [Fact]
    public void Empty_text_returns_no_chunks()
    {
        Assert.Empty(DocumentChunker.Chunk(null));
        Assert.Empty(DocumentChunker.Chunk("  \r\n "));
    }

    [Fact]
    public void Long_text_splits_and_keeps_heading()
    {
        var body = string.Join("\n", Enumerable.Range(0, 40).Select(i =>
            $"第{i}行: 磁盘满排查日志采集中，这一段用于测试分块边界与标题保留，内容需要足够长。"));
        var text = "# 磁盘巡检\n\n" + body;

        var chunks = DocumentChunker.Chunk(text, maxChars: 300, overlapChars: 50);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk)));
        Assert.Contains("# 磁盘巡检", chunks[0]);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 450, $"chunk 过长: {chunk.Length}"));
    }

    [Fact]
    public void Long_single_line_is_hard_split()
    {
        var chunks = DocumentChunker.Chunk(new string('x', 1000), maxChars: 300, overlapChars: 0);
        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 300));
    }

    [Fact]
    public void Heading_inside_code_fence_is_not_used_as_heading()
    {
        var body = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"普通正文第{i}行，用于把文档撑到需要分块的长度。"));
        var text = "```\n# 这不是标题\n```\n" + body;

        var chunks = DocumentChunker.Chunk(text, maxChars: 200, overlapChars: 0);

        Assert.Equal(1, chunks.Sum(chunk => chunk.Split("# 这不是标题").Length - 1));
    }
}
