using LitSSHmcp.App.Views;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class JobListWindowTests
{
    [Fact]
    public void Job_list_response_deserializes_with_mcp_metadata()
    {
        const string text = """
            {"success":true,"jobs":[
              {"jobId":"job_1","operation":"topology_discover","state":"succeeded","progress":0,
               "createdAt":"2026-11-13T16:32:26.2612512+00:00"}
            ],"availableOperations":["topology_discover"],"_meta":{"x":1},"resultType":"complete"}
            """;

        var parsed = JobListWindow.TryParseJobList(text, out var success, out _, out var jobs);

        Assert.True(parsed);
        Assert.True(success);
        Assert.Single(jobs);
        Assert.Equal("job_1", jobs[0].JobId);
    }

    [Fact]
    public void Job_detail_pretty_json_keeps_unicode_and_plus_readable()
    {
        var pretty = JobListWindow.Pretty(
            """{"text":"中文","createdAt":"2026-11-14T03:07:49+00:00"}""");

        Assert.Contains("中文", pretty);
        Assert.Contains("+00:00", pretty);
        Assert.DoesNotContain("\\u0022", pretty);
        Assert.DoesNotContain("\\u002B", pretty);
    }

    [Fact]
    public void Job_detail_pretty_json_unwraps_legacy_escaped_event_message()
    {
        var message = System.Text.Json.JsonSerializer.Serialize(new { server = "虚拟机服务器", phase = "start" });
        var detail = System.Text.Json.JsonSerializer.Serialize(new
        {
            recentEvents = new[] { new { seq = 1, kind = "phase", message } }
        });

        var pretty = JobListWindow.Pretty(detail);

        Assert.Contains("虚拟机服务器", pretty);
        Assert.DoesNotContain("\\u865A", pretty);
    }

    [Fact]
    public void Job_list_parser_unwraps_call_tool_result_content()
    {
        const string text = """
            {"content":[{"type":"text","text":"{\"success\":true,\"jobs\":[{\"jobId\":\"job_2\",\"operation\":\"ssh_snapshot_refresh\",\"state\":\"failed\",\"progress\":100}]}"}],
             "isError":false}
            """;

        var parsed = JobListWindow.TryParseJobList(text, out var success, out _, out var jobs);

        Assert.True(parsed);
        Assert.True(success);
        Assert.Equal("job_2", Assert.Single(jobs).JobId);
    }
}
