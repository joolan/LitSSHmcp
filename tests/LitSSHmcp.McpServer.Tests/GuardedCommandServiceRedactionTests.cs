using System.Diagnostics;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>
/// GuardedCommandService 输出脱敏兜底回归测试：
/// docker inspect / 日志 / JVM 诊断等命令可能把口令、连接串打到 stdout/stderr，
/// Guarded 层出口必须统一脱敏（不依赖底层 SshService 的部分脱敏"碰巧覆盖"）。
/// </summary>
public class GuardedCommandServiceRedactionTests
{
    private const string Secret = "P@ssw0rd-TopSecret";

    private static AppConfig Config() => new()
    {
        Servers = new[] { new SshServerConfig { Id = "s1", Name = "web-01", Host = "10.0.0.1", Password = Secret } }
    };

    private static GuardedCommandService CreateService(StubSsh ssh) => new(
        new StubConfig(Config()),
        new AllowAllFilter(),
        new StubApproval(),
        new NoopAudit(),
        new StubSecurityOptions(),
        ssh);

    [Fact]
    public async Task RunAsync_redacts_server_password_in_output()
    {
        var ssh = new StubSsh
        {
            NextResult = new CommandResult
            {
                Success = true,
                Output = $"MYSQL_PASSWORD={Secret}\nimage: nginx:1.25",
                ExitCode = 0
            }
        };
        var service = CreateService(ssh);

        var outcome = await service.RunAsync("s1", "docker inspect web");

        Assert.True(outcome.Success);
        Assert.DoesNotContain(Secret, outcome.Output);
        Assert.Contains("******", outcome.Output);
        Assert.Contains("nginx:1.25", outcome.Output);   // 其余内容保持原样
    }

    [Fact]
    public async Task RunAsync_redacts_server_password_in_error()
    {
        var ssh = new StubSsh
        {
            NextResult = new CommandResult
            {
                Success = false,
                Output = string.Empty,
                Error = $"connect failed: password {Secret} rejected",
                ExitCode = 1
            }
        };
        var service = CreateService(ssh);

        var outcome = await service.RunAsync("s1", "mysqladmin ping");

        Assert.False(outcome.Success);
        Assert.DoesNotContain(Secret, outcome.Error);
        Assert.Contains("******", outcome.Error!);
    }

    [Fact]
    public async Task RunFleetAsync_redacts_password_in_per_target_output_and_error()
    {
        var ssh = new StubSsh
        {
            NextResult = new CommandResult
            {
                Success = false,
                Output = $"env: DB_PASS={Secret}",
                Error = $"auth failed for {Secret}",
                ExitCode = 1
            }
        };
        var service = CreateService(ssh);

        var outcome = await service.RunFleetAsync(new[] { "s1", "web-01" }, "docker inspect web");

        Assert.False(outcome.Success);
        Assert.All(outcome.Items, item =>
        {
            Assert.DoesNotContain(Secret, item.Output);
            Assert.DoesNotContain(Secret, item.Error);
        });
    }

    // —— 测试替身 ——

    private sealed class StubSsh : ISshService
    {
        public CommandResult? NextResult { get; set; }

        public Task<CommandResult> ExecuteCommandAsync(SshServerConfig server, string command,
            CancellationToken ct = default, int timeoutSeconds = 60) =>
            Task.FromResult(NextResult ?? new CommandResult
            {
                Success = true,
                Output = string.Empty,
                ExitCode = 0,
                Duration = TimeSpan.Zero
            });

        public Task<bool> TestConnectionAsync(SshServerConfig server, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConnectionProbeResult> ProbeConnectionAsync(SshServerConfig server, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CommandResult> ExecuteWithSudoAsync(SshServerConfig server, string command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SshShellSession> OpenShellAsync(SshServerConfig server, string terminalType, uint columns, uint rows, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FileTransferResult> UploadFileAsync(SshServerConfig server, string localPath, string remotePath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FileTransferResult> DownloadFileAsync(SshServerConfig server, string remotePath, string localPath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RemoteFileListResult> ListRemoteFilesAsync(SshServerConfig server, string remotePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoteFileExistsAsync(SshServerConfig server, string remotePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BatchTransferResult> DownloadBatchAsync(SshServerConfig server, IReadOnlyList<string> remotePaths, string localDirectory, bool recursive, int maxFiles, long maxTotalBytes, Func<string, bool>? overwriteResolver = null, CancellationToken ct = default, IProgress<BatchTransferFileProgress>? progress = null) => throw new NotSupportedException();
        public Task<BatchTransferResult> UploadBatchAsync(SshServerConfig server, IReadOnlyList<string> localPaths, string remoteDirectory, bool recursive, int maxFiles, long maxFileBytes, long maxTotalBytes, Func<string, bool>? overwriteResolver = null, CancellationToken ct = default, IProgress<BatchTransferFileProgress>? progress = null) => throw new NotSupportedException();
        public Task<CommandResult> ExecuteStreamingAsync(SshServerConfig server, string command, Action<string>? onStdout = null, Action<string>? onStderr = null, string? stdinText = null, CancellationToken ct = default, int timeoutSeconds = 120) => throw new NotSupportedException();
        public Task<RemoteCopyResult> CopyRemoteToRemoteAsync(RemoteCopyRequest request, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class AllowAllFilter : ICommandFilterService
    {
        public CommandFilterResult CheckCommand(string command) => CommandFilterResult.Allowed;
    }

    private sealed class StubApproval : IApprovalService
    {
        public Task<ApprovalOutcome> RequestApprovalAsync(string serverName, string command,
            CommandFilterResult filterResult, string? filePath = null, string? operationLabel = null,
            CancellationToken ct = default, string? riskReport = null)
            => Task.FromResult(ApprovalOutcome.Approved);
    }

    private sealed class StubConfig : IConfigService
    {
        private readonly AppConfig _config;
        public StubConfig(AppConfig config) => _config = config;
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }

    private sealed class NoopAudit : IAuditLogService
    {
        public string? SessionId { get; set; }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task LogCommandAsync(CommandAuditLog log) => Task.CompletedTask;
        public Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null, AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null) => Task.FromResult(Array.Empty<CommandAuditLog>());
        public Task LogSqlAsync(SqlAuditLog log) => Task.CompletedTask;
        public Task<SqlAuditLog[]> GetSqlLogsAsync(string? dataSourceId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null, AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null) => Task.FromResult(Array.Empty<SqlAuditLog>());
        public Task RecordSessionAsync(AuditSession session) => Task.CompletedTask;
        public Task<AuditSession[]> GetSessionsAsync(int limit = 50, string? ownerUserId = null) => Task.FromResult(Array.Empty<AuditSession>());
        public Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default) => Task.FromResult(new AuditVerifyResult { Ok = true });
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
