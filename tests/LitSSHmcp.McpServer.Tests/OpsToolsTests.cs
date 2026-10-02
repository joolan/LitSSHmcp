using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class OpsToolsTests
{
    [Fact]
    public async Task Docker_logs_quotes_container_and_since()
    {
        var runner = new CapturingRunner();
        var tools = new DockerTools(runner);

        await tools.DockerLogs("s1", "web-1", tail: 10, since: "10m");

        Assert.Equal("docker logs --tail 10 --since '10m' -- 'web-1'", runner.LastCommand);
    }

    [Fact]
    public async Task Docker_logs_rejects_invalid_container_without_running()
    {
        var runner = new CapturingRunner();
        var tools = new DockerTools(runner);

        var result = await tools.DockerLogs("s1", "bad name; rm -rf /", tail: 10);

        Assert.False(result.Success);
        Assert.Equal("invalid_container", result.Status);
        Assert.Null(runner.LastCommand);
    }

    [Fact]
    public async Task Service_status_rejects_invalid_service_without_running()
    {
        var runner = new CapturingRunner();
        var tools = new ServiceTools(runner);

        var result = await tools.ServiceStatus("s1", "nginx; rm -rf /");

        Assert.Equal("invalid_argument", result.Status);
        Assert.Null(runner.LastCommand);
    }

    [Fact]
    public async Task Java_threads_rejects_non_numeric_pid()
    {
        var runner = new CapturingRunner();
        var tools = new JavaTools(runner, new StubConfig(new AppConfig()));

        var result = await tools.JavaThreads("s1", "abc");

        Assert.Equal("invalid_argument", result.Status);
        Assert.Null(runner.LastCommand);
    }

    [Fact]
    public async Task Java_tools_resolve_pid_from_app_name()
    {
        var config = new AppConfig();
        config.Applications = new[]
        {
            new ApplicationConfig { Id = "app1", Name = "order", ContainerName = "order-svc" }
        };
        var runner = new CapturingRunner
        {
            NextOutcome = new GuardedCommandOutcome { ServerFound = true, Success = true, Output = "1234 java -jar /opt/order/order.jar\n" }
        };
        var tools = new JavaTools(runner, new StubConfig(config));

        var result = await tools.JavaInfo("s1", appId: "order");

        Assert.True(result.Success);
        Assert.Contains("jcmd 1234 VM.version", runner.LastCommand);
    }

    [Fact]
    public async Task App_health_snapshot_filters_docker_by_container()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web-01", Host = "10.0.0.1" } },
            Applications = new[] { new ApplicationConfig { Id = "app1", Name = "order", ContainerName = "order-svc", Port = 8080 } },
            Relations = new[] { new RelationConfig { From = "app:app1", To = "ssh:s1", Type = "runsOn" } }
        };
        var runner = new CapturingRunner();
        var tools = new AppTools(new StubConfig(config), runner, new EmptyRegistry());

        var result = await tools.AppHealthSnapshot("order");

        Assert.True(result.Success);
        Assert.Contains("--filter name='order-svc'", runner.LastCommand);
        Assert.Contains(":8080", runner.LastCommand);
    }

    [Fact]
    public async Task Log_tail_blocks_path_outside_whitelist()
    {
        var runner = new CapturingRunner();
        var tools = new LogTools(StubConfig.WithLogPaths("/var/log"), runner);

        var result = await tools.LogTail("s1", "/etc/shadow", lines: 50);

        Assert.False(result.Success);
        Assert.Equal("path_not_allowed", result.Status);
        Assert.Null(runner.LastCommand);
    }

    [Fact]
    public async Task Log_tail_quotes_allowed_path()
    {
        var runner = new CapturingRunner();
        var tools = new LogTools(StubConfig.WithLogPaths("/var/log"), runner);

        var result = await tools.LogTail("s1", "/var/log/my app/app.log", lines: 50);

        Assert.True(result.Success);
        Assert.Equal("tail -n 50 -- '/var/log/my app/app.log'", runner.LastCommand);
    }

    [Fact]
    public async Task Log_grep_treats_no_match_as_success()
    {
        var runner = new CapturingRunner
        {
            NextOutcome = new GuardedCommandOutcome { ServerFound = true, Success = false, ExitCode = 1, Output = string.Empty }
        };
        var tools = new LogTools(StubConfig.WithLogPaths("/var/log"), runner);

        var result = await tools.LogGrep("s1", "Exception", "/var/log/app.log");

        Assert.True(result.Success);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task Log_tail_resolves_path_from_app_config()
    {
        var config = new AppConfig();
        config.Security.Logs.AllowedPaths = new[] { "/var/log" };
        config.Applications = new[]
        {
            new ApplicationConfig { Id = "app1", Name = "order", LogPaths = new[] { "/var/log/order/app.log" } }
        };
        var runner = new CapturingRunner();
        var tools = new LogTools(new StubConfig(config), runner);

        var result = await tools.LogTail("s1", appId: "order", lines: 20);

        Assert.True(result.Success);
        Assert.Equal("tail -n 20 -- '/var/log/order/app.log'", runner.LastCommand);
    }

    [Fact]
    public async Task Log_tail_reports_multiple_app_paths_ambiguity()
    {
        var config = new AppConfig();
        config.Security.Logs.AllowedPaths = new[] { "/var/log" };
        config.Applications = new[]
        {
            new ApplicationConfig { Id = "app1", Name = "order", LogPaths = new[] { "/var/log/order/a.log", "/var/log/order/b.log" } }
        };
        var runner = new CapturingRunner();
        var tools = new LogTools(new StubConfig(config), runner);

        var result = await tools.LogTail("s1", appId: "order");

        Assert.False(result.Success);
        Assert.Equal("log_path_ambiguous", result.Status);
        Assert.Null(runner.LastCommand);
    }

    [Fact]
    public async Task App_health_snapshot_reports_unknown_app()
    {
        var tools = new AppTools(new StubConfig(new AppConfig()), new CapturingRunner(), new EmptyRegistry());

        var result = await tools.AppHealthSnapshot("nope");

        Assert.False(result.Success);
        Assert.Equal("app_not_found", result.Status);
    }

    private sealed class CapturingRunner : IGuardedCommandService
    {
        public string? LastCommand { get; private set; }
        public GuardedCommandOutcome? NextOutcome { get; set; }

        public Task<GuardedCommandOutcome> RunAsync(string serverReference, string command, CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(NextOutcome ?? new GuardedCommandOutcome { ServerFound = true, Success = true, Output = string.Empty });
        }
    }

    private sealed class EmptyRegistry : IDatasourceDriverRegistry
    {
        public IDatasourceDriver? Get(string type) => null;
        public IDatasourceDriver GetRequired(string type) => throw new NotSupportedException(type);
        public IReadOnlyCollection<string> SupportedTypes => Array.Empty<string>();
    }

    private sealed class StubConfig : IConfigService
    {
        private readonly AppConfig _config;

        public StubConfig(AppConfig config) => _config = config;

        public static StubConfig WithLogPaths(params string[] paths)
        {
            var config = new AppConfig();
            config.Security.Logs.AllowedPaths = paths;
            return new StubConfig(config);
        }

        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }
}
