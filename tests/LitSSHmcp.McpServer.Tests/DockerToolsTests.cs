using LitSSHmcp.McpServer.Services;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class DockerToolsTests
{
    [Fact]
    public async Task Docker_exec_quotes_each_argv_element()
    {
        var runner = new CapturingRunner();
        var tools = new DockerTools(runner);

        await tools.DockerExec("s1", "app", "ps -ef", CancellationToken.None);

        Assert.Equal("docker exec 'app' 'ps' '-ef'", runner.Command);
    }

    [Fact]
    public async Task Docker_exec_neutralizes_shell_injection()
    {
        var runner = new CapturingRunner();
        var tools = new DockerTools(runner);

        await tools.DockerExec("s1", "app", "true; touch /tmp/pwn", CancellationToken.None);

        Assert.Equal("docker exec 'app' 'true;' 'touch' '/tmp/pwn'", runner.Command);
    }

    [Fact]
    public async Task Docker_exec_allows_explicit_shell_with_quoted_script()
    {
        var runner = new CapturingRunner();
        var tools = new DockerTools(runner);

        await tools.DockerExec("s1", "app", "sh -c 'touch /tmp/x; echo ok'", CancellationToken.None);

        Assert.Equal("docker exec 'app' 'sh' '-c' 'touch /tmp/x; echo ok'", runner.Command);
    }

    [Fact]
    public async Task Docker_exec_rejects_newlines_without_running()
    {
        var runner = new CapturingRunner();
        var tools = new DockerTools(runner);

        var result = await tools.DockerExec("s1", "app", "echo one\ntwo", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_argument", result.Status);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Docker_exec_rejects_invalid_container_without_running()
    {
        var runner = new CapturingRunner();
        var tools = new DockerTools(runner);

        var result = await tools.DockerExec("s1", "app; evil", "ps", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_container", result.Status);
        Assert.Equal(0, runner.Calls);
    }

    private sealed class CapturingRunner : IGuardedCommandService
    {
        public string? Command { get; private set; }
        public int Calls { get; private set; }

        public Task<GuardedCommandOutcome> RunAsync(string serverReference, string command, CancellationToken ct = default)
        {
            Calls++;
            Command = command;
            return Task.FromResult(new GuardedCommandOutcome
            {
                ServerFound = true,
                Success = true,
                ServerId = serverReference,
                ServerName = "test",
                ServerHost = "host",
                Output = "ok"
            });
        }
    }
}
