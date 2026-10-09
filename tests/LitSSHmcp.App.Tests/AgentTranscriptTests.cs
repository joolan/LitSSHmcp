using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AgentTranscriptTests
{
    [Fact]
    public void Serialize_deserialize_roundtrip_preserves_fields()
    {
        var step = new AgentStep("ssh_execute_command", "{\"serverId\":\"web-01\"}");
        step.Complete(true, "Filesystem ...", 123);

        var json = AgentTranscript.Serialize(step);
        var parsed = AgentTranscript.Deserialize(json);

        Assert.NotNull(parsed);
        Assert.Equal("ssh_execute_command", parsed!.Tool);
        Assert.Equal("{\"serverId\":\"web-01\"}", parsed.Args);
        Assert.Equal("Filesystem ...", parsed.Result);
        Assert.True(parsed.Ok);
        Assert.Equal(123, parsed.Ms);
    }

    [Fact]
    public void Deserialize_returns_null_for_corrupt_content()
    {
        Assert.Null(AgentTranscript.Deserialize("not-json"));
    }

    [Fact]
    public void ToStep_restores_completed_step()
    {
        var step = AgentTranscript.ToStep(new AgentTranscript.Step("t", "{}", "out", false, 42));

        Assert.False(step.IsRunning);
        Assert.False(step.Success);
        Assert.Equal("out", step.Result);
        Assert.Equal(42, step.DurationMs);
        Assert.Equal(System.Windows.Visibility.Visible, step.RetryVisibility);
    }

    [Fact]
    public void BuildTrace_formats_success_and_failure_lines()
    {
        var ok = new AgentStep("ssh_execute_command", "{\"command\":\"df -h\"}");
        ok.Complete(true, "ok-output", 120);
        var fail = new AgentStep("docker_restart", "{}");
        fail.Complete(false, "rejected", 30);

        var trace = AgentTranscript.BuildTrace(new[] { ok, fail });

        Assert.Contains("ssh_execute_command", trace);
        Assert.Contains("→ 成功", trace);
        Assert.Contains("→ 失败", trace);
        Assert.Contains("rejected", trace);
    }

    [Fact]
    public void BuildTrace_caps_total_chars()
    {
        var steps = new List<AgentStep>();
        for (var i = 0; i < 50; i++)
        {
            var step = new AgentStep("t", "{}");
            step.Complete(true, new string('x', 1000), 1);
            steps.Add(step);
        }

        var trace = AgentTranscript.BuildTrace(steps);

        Assert.True(trace.Length <= 3100, $"轨迹未受控: {trace.Length}");
    }

    [Fact]
    public void Serialize_truncates_oversized_result()
    {
        var step = new AgentStep("t", "{}");
        step.Complete(true, new string('x', AgentTranscript.ResultMaxChars + 500), 1);

        var json = AgentTranscript.Serialize(step);

        var parsed = AgentTranscript.Deserialize(json)!;
        Assert.True(parsed.Result!.Length <= AgentTranscript.ResultMaxChars + 20);
    }
}
