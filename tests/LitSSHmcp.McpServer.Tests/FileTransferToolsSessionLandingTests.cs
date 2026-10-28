using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>
/// M1-5（评估报告 §5.2 工具级默认落地 + §5.3.1 会话目录条件注入）在 fileTransfer 的工具级落地校验：
/// 会话目录与归因一样只由宿主 spawn 经环境变量注入（测试里直接置 <see cref="SessionWorkspace.Current"/>）。
/// </summary>
public class FileTransferToolsSessionLandingTests : IDisposable
{
    private readonly string _root;              // 配置允许根（真实临时目录）
    private readonly string _sessionUnderRoot;  // 会话目录在允许根之下 → 注入
    private readonly string _sessionOutsideRoot;// 会话目录在允许根之外 → 不注入

    public FileTransferToolsSessionLandingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "litssh-ft-root-" + Guid.NewGuid().ToString("N"));
        _sessionUnderRoot = Path.Combine(_root, "LitSSH", "jobs", "local", "s1");
        _sessionOutsideRoot = Path.Combine(Path.GetTempPath(), "litssh-ft-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionUnderRoot);
        Directory.CreateDirectory(_sessionOutsideRoot);
        SessionWorkspace.Current = null;
    }

    public void Dispose()
    {
        SessionWorkspace.Current = null;
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
        try { Directory.Delete(_sessionOutsideRoot, true); } catch { /* ignore */ }
    }

    private static AppConfig MakeConfig(string allowedRoot)
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "srv1", Name = "测试机", Host = "192.0.2.1" } }
        };
        config.Security.FileTransfer.Enabled = true;
        config.Security.FileTransfer.RequireApproval = false;
        config.Security.FileTransfer.AllowedLocalPaths = new[] { allowedRoot };
        config.Security.FileTransfer.AllowedRemotePaths = new[] { "/var/log" };
        return config;
    }

    private static (FileTransferTools Tools, StubSsh Ssh) MakeTools(AppConfig config)
    {
        var ssh = new StubSsh();
        var security = new StubSecurityOptions();
        var tools = new FileTransferTools(new StubConfig(config), ssh, new StubApproval(),
            new NoopAudit(), security, new ScriptRiskAnalyzer(security));
        return (tools, ssh);
    }

    [Fact]
    public async Task Download_omitted_localPath_lands_in_session_dir_when_under_allowed_root()
    {
        SessionWorkspace.Current = _sessionUnderRoot;
        var (tools, ssh) = MakeTools(MakeConfig(_root));

        var result = await tools.Download("srv1", "/var/log/app.log");

        Assert.True(result.Success);
        Assert.Null(result.Status);
        Assert.Equal(_sessionUnderRoot, result.LocalDirectory);
        Assert.Equal(_sessionUnderRoot, ssh.LastDownloadBatchDirectory);
    }

    [Fact]
    public async Task Download_relative_localPath_resolves_under_session_dir()
    {
        SessionWorkspace.Current = _sessionUnderRoot;
        var (tools, ssh) = MakeTools(MakeConfig(_root));

        var result = await tools.Download("srv1", "/var/log/app.log", "sub/y.log");

        Assert.True(result.Success);
        var expected = Path.Combine(_sessionUnderRoot, "sub", "y.log");
        Assert.Equal(expected, result.LocalDirectory);
        Assert.Equal(expected, ssh.LastDownloadBatchDirectory);
    }

    [Fact]
    public async Task Download_session_dir_outside_allowed_root_is_not_injected()
    {
        // §5.3.1: 越界会话目录不进白名单——否则等于把任意路径洗进 allowedLocalPaths（§10.C.4）
        SessionWorkspace.Current = _sessionOutsideRoot;
        var (tools, ssh) = MakeTools(MakeConfig(_root));

        var result = await tools.Download("srv1", "/var/log/app.log");

        Assert.False(result.Success);
        Assert.Equal("path_not_allowed", result.Status);
        Assert.Contains(_sessionOutsideRoot, result.Error);
        Assert.Null(ssh.LastDownloadBatchDirectory);   // 落地尝试被拦在白名单，不得触达传输
    }

    [Fact]
    public async Task Download_omitted_localPath_without_session_dir_requires_explicit_path()
    {
        SessionWorkspace.Current = null;          // 旧会话/直连 MCP：现状行为
        var (tools, ssh) = MakeTools(MakeConfig(_root));

        var result = await tools.Download("srv1", "/var/log/app.log");

        Assert.False(result.Success);
        Assert.Equal("bad_request", result.Status);
        Assert.Contains("localDirectory", result.Error);
        Assert.Null(ssh.LastDownloadBatchDirectory);
    }

    [Fact]
    public async Task DownloadFiles_omitted_localDirectory_lands_in_session_dir()
    {
        SessionWorkspace.Current = _sessionUnderRoot;
        var (tools, ssh) = MakeTools(MakeConfig(_root));

        var result = await tools.DownloadFiles("srv1", "/var/log/a.log");

        Assert.True(result.Success);
        Assert.Equal(_sessionUnderRoot, ssh.LastDownloadBatchDirectory);
        Assert.Equal(_sessionUnderRoot, result.LocalDirectory);
    }

    [Fact]
    public async Task Upload_from_session_dir_allowed_only_when_session_under_allowed_root()
    {
        var inRoot = Path.Combine(_sessionUnderRoot, "a.txt");
        File.WriteAllText(inRoot, "x");
        var outside = Path.Combine(_sessionOutsideRoot, "a.txt");
        File.WriteAllText(outside, "x");

        SessionWorkspace.Current = _sessionUnderRoot;
        var (tools, ssh) = MakeTools(MakeConfig(_root));
        var ok = await tools.Upload("srv1", inRoot, "/var/log");
        Assert.True(ok.Success);
        Assert.Equal(new[] { inRoot }, ssh.LastUploadBatchPaths!);

        SessionWorkspace.Current = _sessionOutsideRoot;
        var denied = await tools.Upload("srv1", outside, "/var/log");
        Assert.False(denied.Success);
        Assert.Equal("path_not_allowed", denied.Status);
        // 越界上传没有触达传输层（仍是第一次成功的路径, 未被覆盖）
        Assert.Equal(new[] { inRoot }, ssh.LastUploadBatchPaths!);
    }

    [Fact]
    public async Task Upload_source_outside_session_dir_rejected_when_session_assigned()
    {
        // §5.2 需求 D6 对称约束: 会话已分配时上传源限定在本会话工作目录内(允许根之内也不行)
        var inSession = Path.Combine(_sessionUnderRoot, "ok.txt");
        File.WriteAllText(inSession, "x");
        var sibling = Path.Combine(_root, "LitSSH", "scripts", "note.txt");   // 允许根内但会话目录外
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllText(sibling, "x");

        SessionWorkspace.Current = _sessionUnderRoot;
        var (tools, ssh) = MakeTools(MakeConfig(_root));

        var denied = await tools.Upload("srv1", sibling, "/var/log");
        Assert.False(denied.Success);
        Assert.Equal("path_not_allowed", denied.Status);
        Assert.Contains("会话工作目录", denied.Error);
        Assert.Null(ssh.LastUploadBatchPaths);                       // 未触达传输层

        // Upload 工具内部走同一批处理路径；再直接调 UploadFiles 覆盖批量入口
        var deniedBatch = await tools.UploadFiles("srv1", sibling, "/var/log/");
        Assert.Equal("path_not_allowed", deniedBatch.Status);
        Assert.Contains("会话工作目录", deniedBatch.Error);

        var ok = await tools.Upload("srv1", inSession, "/var/log");
        Assert.True(ok.Success);
        Assert.Equal(new[] { inSession }, ssh.LastUploadBatchPaths!);
    }

    [Fact]
    public async Task Upload_source_constraint_inactive_without_session_dir()
    {
        // 共享区模式(旧会话/直连 MCP): 现状行为——仅白名单约束, 不做会话目录限定
        var sharedFile = Path.Combine(_root, "LitSSH", "scripts", "x.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(sharedFile)!);
        File.WriteAllText(sharedFile, "#!/bin/bash\necho hi");

        SessionWorkspace.Current = null;
        var (tools, ssh) = MakeTools(MakeConfig(_root));

        var ok = await tools.Upload("srv1", sharedFile, "/var/log");
        Assert.True(ok.Success);
        Assert.Equal(new[] { sharedFile }, ssh.LastUploadBatchPaths!);
    }

    private sealed class StubSsh : ISshService
    {
        public string? LastDownloadBatchDirectory { get; private set; }
        public List<string>? LastUploadBatchPaths { get; private set; }

        public Task<FileTransferResult> DownloadFileAsync(SshServerConfig server, string remotePath, string localPath,
            IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(new FileTransferResult { Success = true, Message = "ok" });

        public Task<BatchTransferResult> DownloadBatchAsync(SshServerConfig server, IReadOnlyList<string> remotePaths,
            string localDirectory, bool recursive, int maxFiles, long maxTotalBytes,
            Func<string, bool>? overwriteResolver = null, CancellationToken ct = default)
        {
            LastDownloadBatchDirectory = localDirectory;
            return Task.FromResult(new BatchTransferResult { Success = true, Total = remotePaths.Count, Succeeded = remotePaths.Count });
        }

        public Task<FileTransferResult> UploadFileAsync(SshServerConfig server, string localPath, string remotePath,
            IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(new FileTransferResult { Success = true, Message = "ok" });

        public Task<bool> TestConnectionAsync(SshServerConfig server, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConnectionProbeResult> ProbeConnectionAsync(SshServerConfig server, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CommandResult> ExecuteCommandAsync(SshServerConfig server, string command, CancellationToken ct = default, int timeoutSeconds = 60) => throw new NotSupportedException();
        public Task<CommandResult> ExecuteWithSudoAsync(SshServerConfig server, string command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SshShellSession> OpenShellAsync(SshServerConfig server, string terminalType, uint columns, uint rows, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RemoteFileListResult> ListRemoteFilesAsync(SshServerConfig server, string remotePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoteFileExistsAsync(SshServerConfig server, string remotePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BatchTransferResult> UploadBatchAsync(SshServerConfig server, IReadOnlyList<string> localPaths, string remoteDirectory,
            bool recursive, int maxFiles, long maxFileBytes, long maxTotalBytes, Func<string, bool>? overwriteResolver = null, CancellationToken ct = default)
        {
            LastUploadBatchPaths = localPaths.ToList();
            return Task.FromResult(new BatchTransferResult
            {
                Success = true,
                Total = localPaths.Count,
                Succeeded = localPaths.Count
            });
        }
        public Task<CommandResult> ExecuteStreamingAsync(SshServerConfig server, string command,
            Action<string>? onStdout = null, Action<string>? onStderr = null, string? stdinText = null,
            CancellationToken ct = default, int timeoutSeconds = 120) => throw new NotSupportedException();
        public Task<RemoteCopyResult> CopyRemoteToRemoteAsync(RemoteCopyRequest request,
            IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
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
