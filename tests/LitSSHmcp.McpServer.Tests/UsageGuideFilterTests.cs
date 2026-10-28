using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>验证 mcp_usage_guide 会按启用分组裁剪工具清单与路由文案（不推荐被停用分组的工具）。</summary>
public class UsageGuideFilterTests
{
    [Fact]
    public void BuildGuide_filters_tools_and_routing_by_enabled_groups()
    {
        var enabled = new HashSet<string>(new[] { ToolGroups.Mysql, ToolGroups.Redis }, StringComparer.OrdinalIgnoreCase);

        var json = UsageGuideTools.BuildGuide(enabled);
        using var doc = JsonDocument.Parse(json);

        var names = doc.RootElement.GetProperty("tools").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToArray();

        // 只保留启用的域
        Assert.Contains("mysql_query", names);
        Assert.Contains("redis_read", names);
        Assert.DoesNotContain("docker_ps", names);
        Assert.DoesNotContain("ssh_list_servers", names);
        Assert.DoesNotContain("log_tail", names);

        // 路由文案/取 ID 提示也不得再推荐被停用分组的工具
        Assert.DoesNotContain("docker_ps", json);
        Assert.DoesNotContain("ssh_execute_command", json);
        Assert.DoesNotContain("ssh_list_servers", json);
        // error_handling 也按启用分组裁剪：ssh 禁用时不再推荐快照刷新/查询
        Assert.DoesNotContain("ssh_snapshot_refresh", json);
        Assert.DoesNotContain("ssh_snapshot_get", json);
        Assert.Contains("readonly_statement", json);

        // 明确告知被停用的能力域
        Assert.True(doc.RootElement.TryGetProperty("disabled_groups", out var disabled));
        Assert.True(disabled.GetArrayLength() > 0);
    }

    [Fact]
    public void BuildGuide_summary_omits_tool_list_and_keeps_routing()
    {
        var json = UsageGuideTools.BuildGuide(null, includeInternalTools: true,
            includeToolList: false, summaryOnly: true);
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("tools", out _));
        Assert.True(doc.RootElement.TryGetProperty("server_query_guide", out _));
        Assert.True(doc.RootElement.TryGetProperty("datasource_query_guide", out _));
        Assert.True(doc.RootElement.TryGetProperty("topology_guide", out _));
        Assert.True(doc.RootElement.TryGetProperty("error_handling", out _));
        Assert.True(json.Length < 8000, $"summary guide too long: {json.Length}");
    }

    [Fact]
    public async Task GetUsageGuide_defaults_to_summary_and_full_includes_tools()
    {
        var guideTools = new UsageGuideTools();

        var summary = await guideTools.GetUsageGuide();
        using (var summaryDoc = JsonDocument.Parse(summary))
            Assert.False(summaryDoc.RootElement.TryGetProperty("tools", out _));

        var full = await guideTools.GetUsageGuide("full");
        using var fullDoc = JsonDocument.Parse(full);
        Assert.True(fullDoc.RootElement.TryGetProperty("tools", out var tools));
        Assert.True(tools.GetArrayLength() > 0);
    }

    [Fact]
    public void BuildGuide_null_enabled_keeps_full_catalog_for_host()
    {
        var json = UsageGuideTools.BuildGuide(null, includeInternalTools: true);
        using var doc = JsonDocument.Parse(json);

        // 与 ToolCatalogConsistencyTests.ExpectedToolCount 一致：宿主会话包含内部工具
        Assert.Equal(38, doc.RootElement.GetProperty("tools").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("disabled_groups", out _));
    }

    [Fact]
    public void BuildGuide_hides_internal_tool_for_external_clients()
    {
        var json = UsageGuideTools.BuildGuide(null, includeInternalTools: false);
        using var doc = JsonDocument.Parse(json);

        var names = doc.RootElement.GetProperty("tools").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(37, names.Length);
        Assert.DoesNotContain("mcp_approval_round", names);
        Assert.DoesNotContain("ssh_upload_file", names);
        Assert.DoesNotContain("service_status", names);
        Assert.DoesNotContain("log_tail", names);
        Assert.DoesNotContain("java_threads", names);
        Assert.Contains("ssh_upload", names);
        Assert.Contains("service_query", names);
        Assert.Contains("log_query", names);
        Assert.Contains("java_diagnostics", names);
    }
}
