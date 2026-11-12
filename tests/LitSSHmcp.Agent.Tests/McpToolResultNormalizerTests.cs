using System.Text.Json;
using LitSSHmcp.Agent;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class McpToolResultNormalizerTests
{
    [Fact]
    public void Structured_content_wins_over_duplicate_content()
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "duplicate text" } },
            StructuredContent = JsonSerializer.SerializeToElement(new { success = true, data = new { value = 1 } })
        };

        var normalized = McpToolResultNormalizer.Normalize(result);

        Assert.False(normalized.IsError);
        Assert.Contains("\"success\":true", normalized.Text);
        Assert.DoesNotContain("duplicate text", normalized.Text);
    }

    [Fact]
    public void Falls_back_to_text_when_structured_content_missing()
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "plain result" } }
        };

        var normalized = McpToolResultNormalizer.Normalize(result);

        Assert.False(normalized.IsError);
        Assert.Equal("plain result", normalized.Text);
    }

    [Fact]
    public void Error_prefers_actionable_text()
    {
        var result = new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock> { new TextContentBlock { Text = "server_not_found: web" } },
            StructuredContent = JsonSerializer.SerializeToElement(new { success = false })
        };

        var normalized = McpToolResultNormalizer.Normalize(result);

        Assert.True(normalized.IsError);
        Assert.Contains("server_not_found", normalized.Text);
    }

    [Fact]
    public void Failure_hints_give_next_action_for_common_errors()
    {
        Assert.Contains("ssh_list_servers", ToolFailureHints.Append("ssh_snapshot_get", "server_not_found"));
        Assert.Contains("不要重复", ToolFailureHints.Append("ssh_execute_command", "approval_timeout"));
        Assert.Contains("section", ToolFailureHints.Append("ssh_snapshot_refresh", "timeout"));
        Assert.DoesNotContain("[替代策略]", ToolFailureHints.Append("x", "ok"));
    }

    [Fact]
    public void Non_mcp_result_passes_through()
    {
        var normalized = McpToolResultNormalizer.Normalize("ok");
        Assert.Equal("ok", normalized.Text);
        Assert.False(normalized.IsError);
    }
}
