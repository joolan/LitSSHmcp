using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using LitSSHmcp.McpServer.Services;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class OpsToolsTests
{
    [Fact]
    public async Task Docker_query_logs_quotes_container_and_since()
    {
        var runner = new CapturingRunner();
        var tools = new DockerTools(runner);

        await tools.DockerQuery("s1", "logs", "web-1", tail: 10, since: "10m");

        Assert.Equal("docker logs --tail 10 --since '10m' -- 'web-1'", runner.LastCommand);
    }

    [Fact]
    public async Task Docker_query_logs_rejects_invalid_container_without_running()
    {
        var runner = new CapturingRunner();
        var tools = new DockerTools(runner);

        var result = await tools.DockerQuery("s1", "logs", "bad name; rm -rf /", tail: 10);

        Assert.False(result.Success);
        Assert.Equal("invalid_container", result.Status);
        Assert.Null(runner.LastCommand);
    }

    [Fact]
    public async Task Service_query_rejects_invalid_service_without_running()
    {
        var runner = new CapturingRunner();
        var tools = new ServiceTools(runner);

        var result = await tools.ServiceQuery("s1", "status", "nginx; rm -rf /");

        Assert.Equal("invalid_argument", result.Status);
        Assert.Null(runner.LastCommand);
    }

    [Fact]
    public async Task Java_diagnostics_rejects_non_numeric_pid()
    {
        var runner = new CapturingRunner();
        var tools = new JavaTools(runner, new StubConfig(new AppConfig()));

        var result = await tools.JavaDiagnostics("s1", "threads", "abc");

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

        var result = await tools.JavaDiagnostics("s1", "info", appId: "order");

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
    public async Task Log_query_tail_blocks_path_outside_whitelist()
    {
        var runner = new CapturingRunner();
        var tools = new LogTools(StubConfig.WithLogPaths("/var/log"), runner);

        var result = await tools.LogQuery("s1", "tail", "/etc/shadow", lines: 50);

        Assert.False(result.Success);
        Assert.Equal("path_not_allowed", result.Status);
        Assert.Null(runner.LastCommand);
    }

    [Fact]
    public async Task Log_query_tail_quotes_allowed_path()
    {
        var runner = new CapturingRunner();
        var tools = new LogTools(StubConfig.WithLogPaths("/var/log"), runner);

        var result = await tools.LogQuery("s1", "tail", "/var/log/my app/app.log", lines: 50);

        Assert.True(result.Success);
        Assert.Equal("tail -n 50 -- '/var/log/my app/app.log'", runner.LastCommand);
    }

    [Fact]
    public async Task Log_query_grep_treats_no_match_as_success()
    {
        var runner = new CapturingRunner
        {
            NextOutcome = new GuardedCommandOutcome { ServerFound = true, Success = false, ExitCode = 1, Output = string.Empty }
        };
        var tools = new LogTools(StubConfig.WithLogPaths("/var/log"), runner);

        var result = await tools.LogQuery("s1", "grep", "/var/log/app.log", pattern: "Exception");

        Assert.True(result.Success);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task Log_query_tail_resolves_path_from_app_config()
    {
        var config = new AppConfig();
        config.Security.Logs.AllowedPaths = new[] { "/var/log" };
        config.Applications = new[]
        {
            new ApplicationConfig { Id = "app1", Name = "order", LogPaths = new[] { "/var/log/order/app.log" } }
        };
        var runner = new CapturingRunner();
        var tools = new LogTools(new StubConfig(config), runner);

        var result = await tools.LogQuery("s1", "tail", appId: "order", lines: 20);

        Assert.True(result.Success);
        Assert.Equal("tail -n 20 -- '/var/log/order/app.log'", runner.LastCommand);
    }

    [Fact]
    public async Task Log_query_tail_reports_multiple_app_paths_ambiguity()
    {
        var config = new AppConfig();
        config.Security.Logs.AllowedPaths = new[] { "/var/log" };
        config.Applications = new[]
        {
            new ApplicationConfig { Id = "app1", Name = "order", LogPaths = new[] { "/var/log/order/a.log", "/var/log/order/b.log" } }
        };
        var runner = new CapturingRunner();
        var tools = new LogTools(new StubConfig(config), runner);

        var result = await tools.LogQuery("s1", "tail", appId: "order");

        Assert.False(result.Success);
        Assert.Equal("log_path_ambiguous", result.Status);
        Assert.Null(runner.LastCommand);
    }

    [Fact]
    public async Task Log_query_url_resolves_app_and_server()
    {
        var config = new AppConfig();
        config.Security.Logs.AllowedPaths = new[] { "/var/log" };
        config.Servers = new[]
        {
            new SshServerConfig { Id = "s1", Name = "web-01", Host = "10.0.0.1", AssetKey = "web" }
        };
        config.Applications = new[]
        {
            new ApplicationConfig
            {
                Id = "app1", Name = "order", Type = "java", AssetKey = "order",
                RuntimeKind = RuntimeEnvironmentKind.SshServer, RuntimeSshAssetKey = "web",
                LogPaths = new[] { "/var/log/order/app.log" },
                Urls = new[] { new UrlPattern { Url = "https://shop.example.com/api", Role = UrlRole.Auto } }
            }
        };
        var stub = new StubConfig(config);
        var runner = new CapturingRunner();
        var tools = new LogTools(stub, runner, new UrlResolutionService(stub));

        var result = await tools.LogQuery(url: "https://shop.example.com/api/order", action: "tail", lines: 20);

        Assert.True(result.Success, result.Error);
        Assert.Equal("tail -n 20 -- '/var/log/order/app.log'", runner.LastCommand);
    }

    [Fact]
    public async Task Log_query_url_requires_app_when_multiple_backends_match()
    {
        var config = new AppConfig();
        config.Security.Logs.AllowedPaths = new[] { "/var/log" };
        config.Applications = new[]
        {
            new ApplicationConfig
            {
                Id = "a1", Name = "order-1", AssetKey = "o1", Type = "java",
                RuntimeKind = RuntimeEnvironmentKind.Standalone, LogPaths = new[] { "/var/log/a.log" },
                Urls = new[] { new UrlPattern { Url = "https://shop.example.com/api", Role = UrlRole.Backend } }
            },
            new ApplicationConfig
            {
                Id = "a2", Name = "order-2", AssetKey = "o2", Type = "java",
                RuntimeKind = RuntimeEnvironmentKind.Standalone, LogPaths = new[] { "/var/log/b.log" },
                Urls = new[] { new UrlPattern { Url = "https://shop.example.com/api", Role = UrlRole.Backend } }
            }
        };
        var stub = new StubConfig(config);
        var tools = new LogTools(stub, new CapturingRunner(), new UrlResolutionService(stub));

        var result = await tools.LogQuery(url: "https://shop.example.com/api/order", action: "tail");

        Assert.False(result.Success);
        Assert.Equal("url_ambiguous", result.Status);
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
