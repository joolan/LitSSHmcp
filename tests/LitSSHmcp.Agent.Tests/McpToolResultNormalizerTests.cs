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

    [Fact]
    public void Json_string_envelope_is_unwrapped_without_unicode_escapes()
    {
        var inner = JsonSerializer.Serialize(new
        {
            success = true,
            jobId = "job_x",
            createdAt = "2026-11-14T03:07:49.2705243"
        });
        var outer = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = inner } }
        });

        var normalized = McpToolResultNormalizer.Normalize(outer);

        Assert.Equal(inner, normalized.Text);
        Assert.Contains("\"success\":true", normalized.Text);
        Assert.DoesNotContain("\\u0022", normalized.Text);
    }

    [Fact]
    public void Json_element_envelope_prefers_structured_content()
    {
        using var outer = JsonDocument.Parse("""
            {
              "content": [{"type":"text","text":"duplicate text"}],
              "structuredContent": {"success":true,"jobId":"job_y"}
            }
            """);

        var normalized = McpToolResultNormalizer.Normalize(outer.RootElement);

        Assert.Contains("\"jobId\":\"job_y\"", normalized.Text);
        Assert.DoesNotContain("duplicate text", normalized.Text);
    }

    [Fact]
    public void Ordinary_json_with_content_field_is_not_treated_as_mcp_envelope()
    {
        const string json = """{"content":"normal business data","value":1}""";
        var normalized = McpToolResultNormalizer.Normalize(json);
        Assert.Equal(json, normalized.Text);
    }
}
