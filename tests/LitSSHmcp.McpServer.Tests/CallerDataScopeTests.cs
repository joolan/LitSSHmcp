using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>
/// P0-2：Web 强制模式下全局清单按当前用户授权裁剪（MCP 已不再暴露历史/会话查询工具）。
/// 与授权过滤器共用进程级 env 集合，避免测试并行互相污染。
/// </summary>
[Collection("AuthzEnvironment")]
public class CallerDataScopeTests : IDisposable
{
    private readonly string? _savedMode = Environment.GetEnvironmentVariable(HostEnv.AuthzMode);
    private readonly string? _savedRole = Environment.GetEnvironmentVariable(HostEnv.AuthzRole);
    private readonly string? _savedUser = AuditContext.CurrentUserId;

    public CallerDataScopeTests()
    {
        Environment.SetEnvironmentVariable(HostEnv.AuthzMode, null);
        Environment.SetEnvironmentVariable(HostEnv.AuthzRole, null);
        AuditContext.CurrentUserId = null;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HostEnv.AuthzMode, _savedMode);
        Environment.SetEnvironmentVariable(HostEnv.AuthzRole, _savedRole);
        AuditContext.CurrentUserId = _savedUser;
    }

    private static void Enforce(string? userId, string? role = null)
    {
        Environment.SetEnvironmentVariable(HostEnv.AuthzMode, HostEnv.Enforce);
        Environment.SetEnvironmentVariable(HostEnv.AuthzRole, role);
        AuditContext.CurrentUserId = userId;
    }

    [Fact]
    public async Task Server_list_is_filtered_to_authorized_targets_for_member()
    {
        Enforce("alice");
        var config = ScopedConfig("ssh:s1");
        var tools = new ServerTools(new StubConfig(config), null!, new CapturingAudit());

        var result = await tools.ListServers();

        Assert.True(result.Success);
        var server = Assert.Single(result.Servers);
        Assert.Equal("s1", server.Id);
    }

    [Fact]
    public async Task Server_list_is_complete_for_admin_and_unenforced_hosts()
    {
        var adminConfig = ScopedConfig("ssh:s1");
        Enforce("alice", HostEnv.AdminRole);
        var adminTools = new ServerTools(new StubConfig(adminConfig), null!, new CapturingAudit());
        Assert.Equal(2, (await adminTools.ListServers()).Servers.Count);

        Environment.SetEnvironmentVariable(HostEnv.AuthzMode, null);
        var desktopTools = new ServerTools(new StubConfig(adminConfig), null!, new CapturingAudit());
        Assert.Equal(2, (await desktopTools.ListServers()).Servers.Count);
    }

    [Fact]
    public async Task Datasource_list_hides_unauthorized_targets_and_tunnel_references()
    {
        Enforce("alice");
        var config = ScopedConfig("ssh:s1", "ds:d1");
        var tools = new DatasourceTools(new StubConfig(config), null!, new CapturingAudit());

        var result = await tools.ListDatasources();

        Assert.True(result.Success);
        var ds = Assert.Single(result.DataSources);
        Assert.Equal("d1", ds.Id);
        Assert.Null(ds.TunnelServerId);
        Assert.Equal(new[] { "s1" }, ds.AccessibleFromSshServers);
    }

    private static AppConfig ScopedConfig(params string[] targets) => new()
    {
        Servers = new[]
        {
            new SshServerConfig { Id = "s1", Name = "s1", Host = "10.0.0.1", Username = "root" },
            new SshServerConfig { Id = "s2", Name = "s2", Host = "10.0.0.2", Username = "root" }
        },
        DataSources = new[]
        {
            new DataSourceConfig
            {
                Id = "d1", Name = "d1", Type = "mysql", Host = "10.0.0.3",
                TunnelServerId = "s2", AccessMode = AccessMode.SshTunnel
            },
            new DataSourceConfig { Id = "d2", Name = "d2", Type = "mysql", Host = "10.0.0.4" }
        },
        Relations = new[]
        {
            new RelationConfig { From = AssetNode.Ssh("s1"), To = AssetNode.Ds("d1"), Type = "connectsTo" },
            new RelationConfig { From = AssetNode.Ssh("s2"), To = AssetNode.Ds("d1"), Type = "connectsTo" }
        },
        Security = new SecurityConfig
        {
            Authorization = new AuthorizationConfig
            {
                Enabled = true,
                Users = new[]
                {
                    new AuthorizationUserRule { User = "alice", Targets = targets, Level = "read" }
                }
            }
        }
    };

    private sealed class StubConfig : IConfigService
    {
        public static readonly StubConfig Instance = new(new AppConfig());
        private readonly AppConfig _config;

        public StubConfig(AppConfig config) => _config = config;

        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }

    private sealed class CapturingAudit : IAuditLogService
    {
        public string? SessionId { get; set; }
        public IReadOnlyCollection<string>? LastUserIds { get; private set; }
        public IReadOnlyCollection<string>? LastSqlUserIds { get; private set; }
        public string? LastSessionOwnerUserId { get; private set; }
        public int LogCalls { get; private set; }
        public int SessionCalls { get; private set; }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task LogCommandAsync(CommandAuditLog log) => Task.CompletedTask;

        public Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100, string? keyword = null,
            bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null,
            AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null)
        {
            LogCalls++;
            LastUserIds = userIds;
            return Task.FromResult(Array.Empty<CommandAuditLog>());
        }

        public Task LogSqlAsync(SqlAuditLog log) => Task.CompletedTask;

        public Task<SqlAuditLog[]> GetSqlLogsAsync(string? dataSourceId = null, int limit = 100, string? keyword = null,
            bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null,
            AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null)
        {
            LastSqlUserIds = userIds;
            return Task.FromResult(Array.Empty<SqlAuditLog>());
        }

        public Task RecordSessionAsync(AuditSession session) => Task.CompletedTask;

        public Task<AuditSession[]> GetSessionsAsync(int limit = 50, string? ownerUserId = null)
        {
            SessionCalls++;
            LastSessionOwnerUserId = ownerUserId;
            return Task.FromResult(Array.Empty<AuditSession>());
        }

        public Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuditVerifyResult { Ok = true });
    }
}
