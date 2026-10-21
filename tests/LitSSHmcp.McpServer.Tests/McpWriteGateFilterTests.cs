using System.Runtime.CompilerServices;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>
/// 目标级写互斥过滤器（评估报告 §4.3 / M2-1）：安全目录驱动的写类判定、条件写工具的
/// 只读判定、目标键归一化，以及完整过滤器的占锁/放行/超时/释放语义。
/// </summary>
public class McpWriteGateFilterTests : IDisposable
{
    private readonly string _lockRoot =
        Path.Combine(Path.GetTempPath(), "litssh-m2-gate", Guid.NewGuid().ToString("N"));

    private readonly TimeSpan _savedWriteWait = McpWriteGateFilter.WriteWait;

    public McpWriteGateFilterTests()
    {
        // write_busy 路径会等满 WriteWait, 单测缩短等待避免整类用例拖到秒级
        McpWriteGateFilter.WriteWait = TimeSpan.FromMilliseconds(150);
    }

    public void Dispose()
    {
        McpWriteGateFilter.WriteWait = _savedWriteWait;
        try { Directory.Delete(_lockRoot, recursive: true); }
        catch { /* 残留句柄/已删除时忽略 */ }
    }

    // ---------- 写类工具判定 ----------

    [Theory]
    [InlineData("service_restart", true)]
    [InlineData("docker_restart", true)]
    [InlineData("docker_exec", true)]
    [InlineData("ssh_upload_file", true)]
    [InlineData("ssh_upload_files", true)]
    [InlineData("ssh_execute_sudo", true)]
    [InlineData("ssh_execute_command", true)]
    [InlineData("mysql_execute", true)]
    [InlineData("postgres_execute", true)]
    [InlineData("redis_execute", true)]
    [InlineData("ssh_list_files", false)]
    [InlineData("ssh_download_file", false)]
    [InlineData("mysql_query", false)]
    [InlineData("redis_read", false)]
    [InlineData("ssh_snapshot_refresh", true)]
    [InlineData("topology_discover", false)]
    [InlineData("app_health_snapshot", false)]
    [InlineData("unknown_tool", false)]
    [InlineData(null, false)]
    public void RequiresWriteGate_classifies_tools(string? tool, bool expected) =>
        Assert.Equal(expected, McpWriteGateFilter.RequiresWriteGate(tool));

    // ---------- 条件写工具的只读判定 ----------

    [Fact]
    public void IsReadCall_shell_command_always_requires_write_lock()
    {
        // 任意 shell 都可能写；不再用“首词只读”降低授权或并发保护。
        Assert.False(McpWriteGateFilter.IsReadCall("ssh_execute_command", Args(("command", "df -h")), null));
        Assert.False(McpWriteGateFilter.IsReadCall("ssh_execute_command", Args(("command", "reboot")), null));
        Assert.False(McpWriteGateFilter.IsReadCall("ssh_execute_sudo", Args(("command", "cat x")), null));
        Assert.False(McpWriteGateFilter.IsReadCall("service_restart", null, null));
    }

    [Fact]
    public void IsReadCall_redis_classification()
    {
        Assert.True(McpWriteGateFilter.IsReadCall("redis_execute", Args(("command", "GET user:1")), null));
        Assert.False(McpWriteGateFilter.IsReadCall("redis_execute", Args(("command", "SET k 1")), null));
        Assert.False(McpWriteGateFilter.IsReadCall("redis_execute", Args(("command", "FLUSHDB")), null));
    }

    [Fact]
    public void IsReadCall_sql_classification()
    {
        var sqlFilter = new SqlFilterService(new StubSecurityOptions());

        Assert.True(McpWriteGateFilter.IsReadCall("mysql_execute", Args(("sql", "SELECT 1")), sqlFilter));
        Assert.False(McpWriteGateFilter.IsReadCall("mysql_execute", Args(("sql", "UPDATE t SET a=1")), sqlFilter));
        Assert.False(McpWriteGateFilter.IsReadCall("postgres_execute", Args(("sql", "DELETE FROM t")), sqlFilter));
        // 过滤器缺失时保守判写
        Assert.False(McpWriteGateFilter.IsReadCall("mysql_execute", Args(("sql", "SELECT 1")), null));
    }

    // ---------- 目标键归一化 ----------

    [Fact]
    public async Task ResolveTargetKey_normalizes_id_name_and_datasource()
    {
        var config = new AppConfig();
        config.Servers = new[]
        {
            new SshServerConfig { Id = "s-1", Name = "web1", Host = "10.0.0.1" }
        };
        config.DataSources = new[]
        {
            new DataSourceConfig { Id = "ds-1", Name = "order-mysql", Host = "10.0.0.2", Type = "mysql" }
        };
        using var services = BuildServices(config);

        Assert.Equal("ssh:s-1", await McpWriteGateFilter.ResolveTargetKeyAsync(
            services, "ssh_execute_command", Args(("serverId", "s-1"))));
        Assert.Equal("ssh:s-1", await McpWriteGateFilter.ResolveTargetKeyAsync(
            services, "service_restart", Args(("serverId", "web1"))));
        Assert.Equal("ssh:s-1", await McpWriteGateFilter.ResolveTargetKeyAsync(
            services, "service_restart", Args(("serverId", "10.0.0.1"))));
        Assert.Null(await McpWriteGateFilter.ResolveTargetKeyAsync(
            services, "service_restart", Args(("serverId", "ghost"))));

        Assert.Equal("ds:ds-1", await McpWriteGateFilter.ResolveTargetKeyAsync(
            services, "mysql_execute", Args(("datasourceId", "order-mysql"))));
        Assert.Equal("ds:ds-1", await McpWriteGateFilter.ResolveTargetKeyAsync(
            services, "mysql_execute", Args(("datasourceId", "ds-1"))));
        Assert.Null(await McpWriteGateFilter.ResolveTargetKeyAsync(
            services, "mysql_execute", Args(("datasourceId", "ghost"))));

        // 缺 serverId 参数 → 放行(工具自身会报参数错误)
        Assert.Null(await McpWriteGateFilter.ResolveTargetKeyAsync(
            services, "service_restart", null));
    }

    [Fact]
    public async Task ResolveTargetKey_without_config_service_returns_null()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.Null(await McpWriteGateFilter.ResolveTargetKeyAsync(
            services, "service_restart", Args(("serverId", "anything"))));
        Assert.Null(await McpWriteGateFilter.ResolveTargetKeyAsync(
            null, "service_restart", Args(("serverId", "anything"))));
    }

    // ---------- 完整过滤器 ----------

    [Fact]
    public async Task Read_tool_passes_even_when_target_write_lock_is_held()
    {
        var config = TestConfig();
        using var services = BuildServices(config, _lockRoot);
        var mutex = services.GetRequiredService<ITargetWriteMutex>();
        var (held, _) = await mutex.AcquireAsync("ssh:s-1", TimeSpan.Zero);
        Assert.NotNull(held);

        var called = false;
        var result = await InvokeAsync("ssh_list_files", Args(("serverId", "web1")), services,
            next => { called = true; return NextResult(); });

        Assert.True(called);
        Assert.True(result.IsError is not true);
        held!.Dispose();
    }

    [Fact]
    public async Task Read_looking_shell_command_is_blocked_when_target_write_lock_is_held()
    {
        var config = TestConfig();
        using var services = BuildServices(config, _lockRoot);
        var mutex = services.GetRequiredService<ITargetWriteMutex>();
        var (held, _) = await mutex.AcquireAsync("ssh:s-1", TimeSpan.Zero);
        Assert.NotNull(held);

        var called = false;
        var result = await InvokeAsync("ssh_execute_command",
            Args(("serverId", "web1"), ("command", "df -h")), services,
            next => { called = true; return NextResult(); });

        Assert.False(called);
        Assert.True(result.IsError);
        Assert.Contains("write_busy", Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text);
        held!.Dispose();
    }

    [Fact]
    public async Task Write_tool_is_blocked_with_write_busy_when_target_is_locked()
    {
        var config = TestConfig();
        using var services = BuildServices(config, _lockRoot);
        var mutex = services.GetRequiredService<ITargetWriteMutex>();
        var (held, _) = await mutex.AcquireAsync("ssh:s-1", TimeSpan.Zero);
        Assert.NotNull(held);

        var called = false;
        var result = await InvokeAsync("service_restart", Args(("serverId", "web1")), services,
            next => { called = true; return NextResult(); });

        Assert.False(called);
        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.Contains("write_busy", text);
        Assert.Contains("ssh:s-1", text);

        held!.Dispose();
    }

    [Fact]
    public async Task Write_shell_command_is_blocked_when_target_is_locked()
    {
        var config = TestConfig();
        using var services = BuildServices(config, _lockRoot);
        var mutex = services.GetRequiredService<ITargetWriteMutex>();
        var (held, _) = await mutex.AcquireAsync("ssh:s-1", TimeSpan.Zero);
        Assert.NotNull(held);

        var result = await InvokeAsync("ssh_execute_command",
            Args(("serverId", "web1"), ("command", "reboot")), services,
            next => NextResult());

        Assert.True(result.IsError);
        held!.Dispose();
    }

    [Fact]
    public async Task Write_tool_acquires_lock_and_releases_it_afterwards()
    {
        var config = TestConfig();
        using var services = BuildServices(config, _lockRoot);
        var mutex = services.GetRequiredService<ITargetWriteMutex>();

        var called = false;
        var result = await InvokeAsync("service_restart", Args(("serverId", "web1")), services,
            next => { called = true; return NextResult(); });

        Assert.True(called);
        Assert.True(result.IsError is not true);

        // next 返回后租约必须已释放 → 同目标可立刻再次获取
        var (again, reason) = await mutex.AcquireAsync("ssh:s-1", TimeSpan.Zero);
        Assert.NotNull(again);
        Assert.Null(reason);
        again!.Dispose();
    }

    [Fact]
    public async Task Lock_is_released_even_when_next_throws()
    {
        var config = TestConfig();
        using var services = BuildServices(config, _lockRoot);
        var mutex = services.GetRequiredService<ITargetWriteMutex>();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync("service_restart", Args(("serverId", "web1")), services,
                next => throw new InvalidOperationException("boom")));

        var (again, reason) = await mutex.AcquireAsync("ssh:s-1", TimeSpan.Zero);
        Assert.NotNull(again);
        Assert.Null(reason);
        again!.Dispose();
    }

    [Fact]
    public async Task Unresolvable_target_passes_through()
    {
        var config = TestConfig();
        using var services = BuildServices(config, _lockRoot);

        var called = false;
        var result = await InvokeAsync("service_restart", Args(("serverId", "ghost")), services,
            next => { called = true; return NextResult(); });

        Assert.True(called);
        Assert.True(result.IsError is not true);
    }

    // ---------- 辅助 ----------

    private static AppConfig TestConfig()
    {
        var config = new AppConfig();
        config.Servers = new[]
        {
            new SshServerConfig { Id = "s-1", Name = "web1", Host = "10.0.0.1" }
        };
        return config;
    }

    private static ServiceProvider BuildServices(AppConfig config, string? lockRoot = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigService>(new StubConfig(config));
        services.AddSingleton<ISqlFilterService>(new SqlFilterService(new StubSecurityOptions()));
        services.AddSingleton<ITargetWriteMutex>(new FileTargetWriteMutex(
            lockRoot ?? Path.Combine(Path.GetTempPath(), "litssh-m2-gate-unused")));
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, JsonElement> Args(params (string Name, string Value)[] values)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (name, value) in values)
            args[name] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    private static CallToolResult NextResult() => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = "NEXT" } }
    };

    private static async Task<CallToolResult> InvokeAsync(
        string tool,
        Dictionary<string, JsonElement>? args,
        IServiceProvider services,
        Func<bool, CallToolResult> next)
    {
        var requestParams = new CallToolRequestParams
        {
            Name = tool,
            Arguments = args
        };
        // RequestContext 构造要求非空 McpServer（真实服务器依赖传输层, 单测用不到）——
        // 绕过构造函数创建未初始化实例, 再只回填过滤器实际读取的 Params/Services 属性。
        var context = (RequestContext<CallToolRequestParams>)RuntimeHelpers.GetUninitializedObject(
            typeof(RequestContext<CallToolRequestParams>));
        context.Params = requestParams;
        context.Services = services;

        // 各测试通过自己的闭包副作用断言 next 是否被调用, 此处入参仅为统一委托形状
        McpRequestHandler<CallToolRequestParams, CallToolResult> handler = (_, _) =>
            ValueTask.FromResult(next(false));

        var filter = McpWriteGateFilter.CreateFilter();
        return await filter(handler)(context, CancellationToken.None);
    }

    private sealed class StubConfig : IConfigService
    {
        private readonly AppConfig _config;

        public StubConfig(AppConfig config) => _config = config;

        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }

    private sealed class StubSecurityOptions : ISecurityOptionsProvider
    {
        public bool Enabled { get; set; } = true;
        public CommandFilterConfig CommandFilter { get; set; } = new();
        public SqlFilterConfig SqlFilter { get; set; } = new();
        public ScriptRiskConfig ScriptRisk { get; set; } = new();
        public FileTransferConfig FileTransfer { get; set; } = new();
        public SshHostKeyConfig SshHostKey { get; set; } = new();
        public DiscoveryConfig Discovery { get; set; } = new();
        public LimitsConfig Limits { get; set; } = new();
        public AuditConfig Audit { get; set; } = new();
        public ApprovalConfig Approval { get; set; } = new();
        public ConnectionPoolConfig ConnectionPool { get; set; } = new();
        public void Invalidate() { }
    }
}
