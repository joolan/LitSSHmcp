using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using LitSSHmcp.McpServer.Services;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public sealed class TopologyJobHintTests
{
    [Fact]
    public async Task Sync_discovery_reports_running_job_id_and_progress()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-topology-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var config = new AppConfig
            {
                Security = new SecurityConfig
                {
                    Discovery = new DiscoveryConfig { AllowedSearchPaths = Array.Empty<string>() }
                }
            };
            var store = new SqliteJobStore(Path.Combine(dir, "jobs.db"));
            var jobs = new JobManager(store, new StubConfig(config));
            await store.SaveAsync(new JobState
            {
                Id = "job_topology",
                Operation = "topology_discover",
                State = JobStates.Running,
                OwnerUser = "alice",
                SessionId = "session-1",
                ProcessId = Environment.ProcessId,
                HeartbeatAt = DateTimeOffset.UtcNow,
                ProgressDone = 2,
                ProgressTotal = 5,
                Progress = 40
            });

            var tools = new TopologyTools(new ThrowingTopology(), new StubConfig(config),
                new AllowAllFilter(), new ApprovedApproval(), new NoopAudit(), new StubSecurityOptions(), jobs);

            var result = await tools.DiscoverTopology();

            Assert.False(result.Success);
            Assert.Equal("discovery_in_progress", result.Status);
            Assert.Contains("job_topology", result.Error);
            Assert.Contains("2/5", result.Error);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    private sealed class StubConfig(AppConfig config) : IConfigService
    {
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }

    private sealed class ThrowingTopology : ITopologyService
    {
        public Task<TopologyGraph> GetGraphAsync(CancellationToken ct = default) => Task.FromResult(new TopologyGraph());
        public Task<DependencyGraph> GetDependenciesAsync(string assetId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DiscoveryResult> DiscoverAsync(string[]? serverIds, string[]? searchPaths,
            CancellationToken ct = default, IProgress<TopologyDiscoveryProgress>? progress = null) =>
            throw new DiscoveryInProgressException();
    }

    private sealed class AllowAllFilter : ICommandFilterService
    {
        public CommandFilterResult CheckCommand(string command) => CommandFilterResult.Allowed;
    }

    private sealed class ApprovedApproval : IApprovalService
    {
        public Task<ApprovalOutcome> RequestApprovalAsync(string serverName, string command,
            CommandFilterResult filterResult, string? filePath = null, string? operationLabel = null,
            CancellationToken ct = default, string? riskReport = null) =>
            Task.FromResult(ApprovalOutcome.Approved);
    }

    private sealed class NoopAudit : IAuditLogService
    {
        public string? SessionId { get; set; }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task LogCommandAsync(CommandAuditLog log) => Task.CompletedTask;
        public Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100, string? keyword = null,
            bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null,
            AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null) =>
            Task.FromResult(Array.Empty<CommandAuditLog>());
        public Task LogSqlAsync(SqlAuditLog log) => Task.CompletedTask;
        public Task<SqlAuditLog[]> GetSqlLogsAsync(string? dataSourceId = null, int limit = 100, string? keyword = null,
            bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null,
            AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null) =>
            Task.FromResult(Array.Empty<SqlAuditLog>());
        public Task RecordSessionAsync(AuditSession session) => Task.CompletedTask;
        public Task<AuditSession[]> GetSessionsAsync(int limit = 50, string? ownerUserId = null) =>
            Task.FromResult(Array.Empty<AuditSession>());
        public Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuditVerifyResult { Ok = true });
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
