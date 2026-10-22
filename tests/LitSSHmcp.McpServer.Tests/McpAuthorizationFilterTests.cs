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
/// 授权矩阵强制过滤器（评估报告 D1 / §10.B.4）：矩阵判定纯逻辑（用户/目标匹配、级别、fail-closed）、
/// 强制开关（宿主 env 注入）、完整过滤器的放行/拒绝/审计语义。
/// </summary>
[Collection("AuthzEnvironment")]
public class McpAuthorizationFilterTests : IDisposable
{
    private readonly string? _savedMode = Environment.GetEnvironmentVariable(HostEnv.AuthzMode);
    private readonly string? _savedAuthzUser = Environment.GetEnvironmentVariable(HostEnv.AuthzUser);
    private readonly string? _savedAuthzRole = Environment.GetEnvironmentVariable(HostEnv.AuthzRole);

    public McpAuthorizationFilterTests()
    {
        // 强制开关是进程级 env：每例开始先复位，避免同类其它用例受残留影响
        Environment.SetEnvironmentVariable(HostEnv.AuthzMode, null);
        Environment.SetEnvironmentVariable(HostEnv.AuthzUser, null);
        Environment.SetEnvironmentVariable(HostEnv.AuthzRole, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HostEnv.AuthzMode, _savedMode);
        Environment.SetEnvironmentVariable(HostEnv.AuthzUser, _savedAuthzUser);
        Environment.SetEnvironmentVariable(HostEnv.AuthzRole, _savedAuthzRole);
    }

    private static void EnableEnforce(string? authzUser = null)
    {
        Environment.SetEnvironmentVariable(HostEnv.AuthzMode, HostEnv.Enforce);
        Environment.SetEnvironmentVariable(HostEnv.AuthzUser, authzUser);
    }

    // ---------- 矩阵判定纯逻辑 ----------

    [Theory]
    [InlineData("alice", true)]
    [InlineData("Alice", true)]   // 用户名忽略大小写
    [InlineData("bob", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MatchesUser_by_username(string? user, bool expected)
    {
        var rule = new AuthorizationUserRule { User = user ?? string.Empty };
        Assert.Equal(expected, McpAuthorizationFilter.MatchesUser(rule, "alice", "7"));
    }

    [Theory]
    [InlineData("7", true)]
    [InlineData("8", false)]
    public void MatchesUser_by_id_when_username_not_configured(string userId, bool expected)
    {
        var rule = new AuthorizationUserRule { User = "7" };
        Assert.Equal(expected, McpAuthorizationFilter.MatchesUser(rule, null, userId));
        Assert.Equal(expected, McpAuthorizationFilter.MatchesUser(rule, "alice", userId)); // Id 匹配兜底
    }

    [Fact]
    public void MatchesUser_wildcard_matches_everyone()
    {
        var rule = new AuthorizationUserRule { User = "*" };
        Assert.True(McpAuthorizationFilter.MatchesUser(rule, "anyone", "42"));
        Assert.True(McpAuthorizationFilter.MatchesUser(rule, null, null));
    }

    [Theory]
    [InlineData("ssh:s-1", "ssh:s-1", true)]
    [InlineData("ssh:*", "ssh:s-1", true)]
    [InlineData("ssh:*", "ds:ds-1", false)]
    [InlineData("ds:*", "ds:ds-1", true)]
    [InlineData("*", "ssh:s-1", true)]
    [InlineData("*", "ds:ds-1", true)]
    [InlineData("ssh:s-10", "ssh:s-1", false)]   // 前缀通配只在末位 * 时生效
    [InlineData("ssh:s-1", "ssh:s-10", false)]   // 精确匹配不做前缀扩展
    [InlineData("", "ssh:s-1", false)]
    public void MatchesTarget_patterns(string pattern, string targetKey, bool expected) =>
        Assert.Equal(expected, McpAuthorizationFilter.MatchesTarget(new[] { pattern }, targetKey));

    [Theory]
    [InlineData("exec", 3)]
    [InlineData("EXEC", 3)]
    [InlineData("write", 2)]
    [InlineData("WRITE", 2)]
    [InlineData("read", 1)]
    [InlineData("bogus", 1)]   // 未知级别按只读，宁严勿松
    [InlineData("", 1)]
    [InlineData(null, 1)]
    public void LevelRank_is_conservative(string? level, int expected) =>
        Assert.Equal(expected, McpAuthorizationFilter.LevelRank(level));

    [Fact]
    public void Evaluate_denies_user_without_rule()
    {
        var (allowed, reason) = McpAuthorizationFilter.Evaluate(
            new AuthorizationConfig(), "alice", "1", "ssh:s-1", isWrite: false);
        Assert.False(allowed);
        Assert.Contains("不在授权矩阵", reason);
    }

    [Fact]
    public void Evaluate_denies_target_outside_whitelist()
    {
        var policy = Policy(("alice", new[] { "ssh:s-1" }, "exec"));
        var (allowed, reason) = McpAuthorizationFilter.Evaluate(policy, "alice", null, "ssh:s-2", isWrite: false);
        Assert.False(allowed);
        Assert.Contains("ssh:s-2", reason);
    }

    [Fact]
    public void Evaluate_read_level_allows_reads_blocks_writes()
    {
        var policy = Policy(("alice", new[] { "ssh:*" }, "read"));

        var (readAllowed, _) = McpAuthorizationFilter.Evaluate(policy, "alice", null, "ssh:s-1", isWrite: false);
        Assert.True(readAllowed);

        var (writeAllowed, reason) = McpAuthorizationFilter.Evaluate(policy, "alice", null, "ssh:s-1", isWrite: true);
        Assert.False(writeAllowed);
        Assert.Contains("低于要求", reason);
    }

    [Fact]
    public void Evaluate_exec_level_allows_writes()
    {
        var policy = Policy(("alice", new[] { "ssh:s-1" }, "exec"), ("*", new[] { "ds:*" }, "read"));
        Assert.True(McpAuthorizationFilter.Evaluate(policy, "alice", null, "ssh:s-1", isWrite: true).Allowed);
        Assert.True(McpAuthorizationFilter.Evaluate(policy, "bob", "9", "ds:ds-1", isWrite: false).Allowed);
        // 兜底规则给了 bob 只读 ds，但写操作仍拒
        Assert.False(McpAuthorizationFilter.Evaluate(policy, "bob", "9", "ds:ds-1", isWrite: true).Allowed);
    }

    [Fact]
    public void Evaluate_write_level_allows_writes_but_not_exec()
    {
        var policy = Policy(("alice", new[] { "ssh:s-1" }, "write"));

        Assert.True(McpAuthorizationFilter.EvaluateAccess(
            policy, "alice", null, new[] { "ssh:s-1" }, ToolAccessLevel.Read).Allowed);
        Assert.True(McpAuthorizationFilter.EvaluateAccess(
            policy, "alice", null, new[] { "ssh:s-1" }, ToolAccessLevel.Write).Allowed);

        var (allowed, reason) = McpAuthorizationFilter.EvaluateAccess(
            policy, "alice", null, new[] { "ssh:s-1" }, ToolAccessLevel.Exec);
        Assert.False(allowed);
        Assert.Contains("exec", reason);
    }

    [Fact]
    public void EvaluateAccess_requires_every_target()
    {
        var policy = Policy(
            ("alice", new[] { "ssh:s-1" }, "exec"),
            ("alice", new[] { "ds:ds-1" }, "read"));

        Assert.True(McpAuthorizationFilter.EvaluateAccess(
            policy, "alice", null, new[] { "ssh:s-1", "ds:ds-1" }, ToolAccessLevel.Read).Allowed);

        var (allowed, reason) = McpAuthorizationFilter.EvaluateAccess(
            policy, "alice", null, new[] { "ssh:s-1", "ds:ds-1" }, ToolAccessLevel.Write);
        Assert.False(allowed);
        Assert.Contains("ds:ds-1", reason);
    }

    [Fact]
    public void Evaluate_disabled_policy_passes_everything()
    {
        var policy = new AuthorizationConfig { Enabled = false };
        Assert.True(McpAuthorizationFilter.Evaluate(policy, null, null, "ssh:s-1", isWrite: true).Allowed);
    }

    // ---------- 完整过滤器 ----------

    [Fact]
    public async Task Off_mode_passes_without_any_rule()
    {
        // 桌面进程：未注入 LITSSH_AUTHZ_MODE → 矩阵不生效（D1 桌面仅归因不限权）
        var called = false;
        var result = await InvokeAsync("service_restart", Args(("serverId", "web1")),
            BuildServices(TestConfig()),
            next => { called = true; return NextResult(); });

        Assert.True(called);
        Assert.True(result.IsError is not true);
    }

    [Fact]
    public async Task Enforce_denies_user_without_rule_and_records_audit()
    {
        EnableEnforce("alice");
        var audit = new CapturingAudit();
        var services = BuildServices(TestConfig(), audit);

        var called = false;
        var result = await InvokeAsync("ssh_list_files", Args(("serverId", "web1")), services,
            next => { called = true; return NextResult(); });

        Assert.False(called);
        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.Contains("authorization_denied", text);
        Assert.Contains("security.authorization", text);

        // 拒绝入审计链：Gate/Blocked + decision（SessionId/Tool/UserId 由服务自动归集）
        var log = Assert.Single(audit.Logs);
        Assert.Equal(CommandStatus.Blocked, log.Status);
        Assert.Equal(AuditCategory.Gate, log.Category);
        Assert.Equal("authorization_denied", log.Decision);
        Assert.Equal("ssh_list_files", log.Tool);
        Assert.Equal("s-1", log.ServerId);
        Assert.Equal("web1", log.ServerName);
    }

    [Fact]
    public async Task Rule_for_other_user_does_not_apply()
    {
        EnableEnforce("alice");
        var config = TestConfig();
        config.Security.Authorization = Policy(("bob", new[] { "*" }, "exec"));
        var services = BuildServices(config);

        var called = false;
        var result = await InvokeAsync("service_restart", Args(("serverId", "web1")), services,
            next => { called = true; return NextResult(); });

        Assert.False(called);
        Assert.True(result.IsError);
        Assert.Contains("不在授权矩阵", Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text);
    }

    [Fact]
    public async Task Audit_failure_does_not_block_denial()
    {
        EnableEnforce("alice");
        var services = BuildServices(TestConfig(), new ThrowingAudit());

        var called = false;
        var result = await InvokeAsync("service_restart", Args(("serverId", "web1")), services,
            next => { called = true; return NextResult(); });

        Assert.False(called);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Enforce_allows_whitelisted_read_but_denies_write()
    {
        EnableEnforce("alice");
        var config = TestConfig();
        config.Security.Authorization = Policy(("alice", new[] { "ssh:s-1" }, "read"));
        var services = BuildServices(config);

        var readCalled = false;
        var read = await InvokeAsync("ssh_list_files", Args(("serverId", "web1")), services,
            next => { readCalled = true; return NextResult(); });
        Assert.True(readCalled);
        Assert.True(read.IsError is not true);

        var writeCalled = false;
        var write = await InvokeAsync("service_restart", Args(("serverId", "web1")), services,
            next => { writeCalled = true; return NextResult(); });
        Assert.False(writeCalled);
        Assert.True(write.IsError);
        Assert.Contains("write", Assert.IsType<TextContentBlock>(Assert.Single(write.Content!)).Text);
    }

    [Fact]
    public async Task Enforce_allows_write_with_exec_rule()
    {
        EnableEnforce("alice");
        var config = TestConfig();
        config.Security.Authorization = Policy(("alice", new[] { "ssh:*" }, "exec"));
        var services = BuildServices(config);

        var called = false;
        var result = await InvokeAsync("service_restart", Args(("serverId", "web1")), services,
            next => { called = true; return NextResult(); });

        Assert.True(called);
        Assert.True(result.IsError is not true);
    }

    [Fact]
    public async Task Enforce_write_level_allows_restart_but_denies_shell_exec()
    {
        EnableEnforce("alice");
        var config = TestConfig();
        config.Security.Authorization = Policy(("alice", new[] { "ssh:*" }, "write"));
        var services = BuildServices(config);

        var restartCalled = false;
        var restart = await InvokeAsync("service_restart", Args(("serverId", "web1")), services,
            next => { restartCalled = true; return NextResult(); });
        Assert.True(restartCalled);
        Assert.True(restart.IsError is not true);

        var shellCalled = false;
        var shell = await InvokeAsync("ssh_execute_command",
            Args(("serverId", "web1"), ("command", "df -h")), services,
            next => { shellCalled = true; return NextResult(); });
        Assert.False(shellCalled);
        Assert.True(shell.IsError);
        Assert.Contains("exec", Assert.IsType<TextContentBlock>(Assert.Single(shell.Content!)).Text);
    }

    [Fact]
    public async Task Enforce_rule_matches_username_injected_by_host()
    {
        EnableEnforce("alice");
        var config = TestConfig();
        config.Security.Authorization = Policy(("alice", new[] { "*" }, "exec"));
        var services = BuildServices(config);

        var called = false;
        var result = await InvokeAsync("service_restart", Args(("serverId", "web1")), services,
            next => { called = true; return NextResult(); });
        Assert.True(called);
        Assert.True(result.IsError is not true);
    }

    [Fact]
    public async Task Enforce_rule_matches_owner_id_when_username_absent()
    {
        EnableEnforce(authzUser: null);
        AuditContext.CurrentUserId = "7";
        var config = TestConfig();
        config.Security.Authorization = Policy(("7", new[] { "*" }, "exec"));
        var services = BuildServices(config);

        var called = false;
        var result = await InvokeAsync("service_restart", Args(("serverId", "web1")), services,
            next => { called = true; return NextResult(); });
        Assert.True(called);
        Assert.True(result.IsError is not true);
    }

    [Fact]
    public async Task Non_target_tool_passes_under_enforce()
    {
        EnableEnforce("alice");
        var services = BuildServices(TestConfig()); // 空矩阵（默认拒绝）也不拦无目标工具

        var called = false;
        var result = await InvokeAsync("mcp_self_check", null, services,
            next => { called = true; return NextResult(); });

        Assert.True(called);
        Assert.True(result.IsError is not true);
    }

    [Fact]
    public async Task Global_query_tools_require_admin_under_enforce()
    {
        EnableEnforce("alice");
        var services = BuildServices(TestConfig()); // 空矩阵：普通成员不得发起无法裁剪的全局查询

        var called = false;
        var result = await InvokeAsync("topology_query", Args(("action", "search")), services,
            next => { called = true; return NextResult(); });

        Assert.False(called);
        Assert.True(result.IsError);
        Assert.Contains("仅管理员", Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text);

        Environment.SetEnvironmentVariable(HostEnv.AuthzRole, HostEnv.AdminRole);
        var adminCalled = false;
        var adminResult = await InvokeAsync("topology_query", Args(("action", "search")), services,
            next => { adminCalled = true; return NextResult(); });

        Assert.True(adminCalled);
        Assert.True(adminResult.IsError is not true);
    }

    [Fact]
    public async Task Self_scoped_global_list_tools_pass_for_member()
    {
        EnableEnforce("alice");
        var services = BuildServices(TestConfig());

        var called = false;
        var result = await InvokeAsync("ssh_list_servers", null, services,
            next => { called = true; return NextResult(); });

        Assert.True(called);
        Assert.True(result.IsError is not true);
    }

    [Fact]
    public async Task Unresolvable_target_is_denied_under_enforce()
    {
        EnableEnforce("alice");
        var services = BuildServices(TestConfig());

        var called = false;
        var result = await InvokeAsync("service_restart", Args(("serverId", "ghost")), services,
            next => { called = true; return NextResult(); });

        Assert.False(called);
        Assert.True(result.IsError);
        Assert.Contains("authorization_denied", Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text);
    }

    [Fact]
    public async Task Shell_command_requires_exec_even_for_read_only_command()
    {
        // shell 不再按“首词只读”降级授权：read 级用户执行 df -h 也拒绝。
        var config = TestConfig();
        config.Security.Authorization = Policy(("alice", new[] { "ssh:s-1" }, "read"));
        var services = BuildServices(config);
        EnableEnforce("alice");

        var called = false;
        var result = await InvokeAsync("ssh_execute_command",
            Args(("serverId", "web1"), ("command", "df -h")), services,
            next => { called = true; return NextResult(); });

        Assert.False(called);
        Assert.True(result.IsError);
        Assert.Contains("exec", Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text);
    }

    [Fact]
    public async Task Shell_command_allowed_with_exec_rule()
    {
        var config = TestConfig();
        config.Security.Authorization = Policy(("alice", new[] { "ssh:s-1" }, "exec"));
        var services = BuildServices(config);
        EnableEnforce("alice");

        var called = false;
        var result = await InvokeAsync("ssh_execute_command",
            Args(("serverId", "web1"), ("command", "df -h")), services,
            next => { called = true; return NextResult(); });

        Assert.True(called);
        Assert.True(result.IsError is not true);
    }

    // ---------- 辅助 ----------

    private static AuthorizationConfig Policy(params (string User, string[] Targets, string Level)[] rules) => new()
    {
        Users = rules.Select(r => new AuthorizationUserRule
        {
            User = r.User,
            Targets = r.Targets,
            Level = r.Level
        }).ToArray()
    };

    private static AppConfig TestConfig() => new()
    {
        Servers = new[]
        {
            new SshServerConfig { Id = "s-1", Name = "web1", Host = "10.0.0.1" }
        }
    };

    private static ServiceProvider BuildServices(AppConfig config, IAuditLogService? audit = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigService>(new StubConfig(config));
        services.AddSingleton<ISqlFilterService>(new SqlFilterService(new StubSecurityOptions()));
        services.AddSingleton(audit ?? new NoopAudit());
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
        // 与 McpWriteGateFilterTests 同一手法：RequestContext 构造依赖真实 MCP 服务器，
        // 用未初始化实例只回填过滤器读取的 Params/Services。
        var context = (RequestContext<CallToolRequestParams>)RuntimeHelpers.GetUninitializedObject(
            typeof(RequestContext<CallToolRequestParams>));
        context.Params = requestParams;
        context.Services = services;

        McpRequestHandler<CallToolRequestParams, CallToolResult> handler = (_, _) =>
            ValueTask.FromResult(next(false));

        var filter = McpAuthorizationFilter.CreateFilter();
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

    private sealed class CapturingAudit : NoopAuditBase
    {
        public List<CommandAuditLog> Logs { get; } = new();

        public override Task LogCommandAsync(CommandAuditLog log)
        {
            Logs.Add(log);
            return Task.CompletedTask;
        }
    }

    private abstract class NoopAuditBase : IAuditLogService
    {
        public string? SessionId { get; set; }
        public virtual Task InitializeAsync() => Task.CompletedTask;
        public virtual Task LogCommandAsync(CommandAuditLog log) => Task.CompletedTask;
        public virtual Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null, AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null) => Task.FromResult(Array.Empty<CommandAuditLog>());
        public virtual Task LogSqlAsync(SqlAuditLog log) => Task.CompletedTask;
        public virtual Task<SqlAuditLog[]> GetSqlLogsAsync(string? dataSourceId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null, AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null) => Task.FromResult(Array.Empty<SqlAuditLog>());
        public virtual Task RecordSessionAsync(AuditSession session) => Task.CompletedTask;
        public virtual Task<AuditSession[]> GetSessionsAsync(int limit = 50, string? ownerUserId = null) => Task.FromResult(Array.Empty<AuditSession>());
        public virtual Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default) => Task.FromResult(new AuditVerifyResult { Ok = true });
    }

    private sealed class NoopAudit : NoopAuditBase
    {
    }

    private sealed class ThrowingAudit : NoopAuditBase
    {
        public override Task LogCommandAsync(CommandAuditLog log) =>
            throw new InvalidOperationException("audit unavailable");
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
