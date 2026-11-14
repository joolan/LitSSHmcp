using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>
/// 长任务 Job 模型的端到端单元测试（设计：maybe-to/2026-11-13-长任务统一Job模型设计.md）。
/// 覆盖状态机、进度事件落库、取消标记、orphan 清理、TTL/上限清理、授权路由与可见性。
/// </summary>
public sealed class JobToolsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly SqliteJobStore _store;
    private readonly JobManager _manager;
    private readonly McpSessionTracker _session;
    private readonly JobTools _tools;
    private readonly AppConfig _config;
    private readonly string? _previousUser;

    public JobToolsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "litssh-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "jobs.db");
        _store = new SqliteJobStore(_dbPath);
        _config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web-1", Host = "10.0.0.1" }
            }
        };
        _manager = new JobManager(_store, new StubConfig(_config));
        _session = new McpSessionTracker("session-1");
        _tools = new JobTools(_manager, _store, _session, new NoopAudit());

        _previousUser = AuditContext.CurrentUserId;
        AuditContext.CurrentUserId = "alice";
    }

    public void Dispose()
    {
        AuditContext.CurrentUserId = _previousUser;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* 测试清理失败忽略 */ }
    }

    [Fact]
    public async Task Start_progress_events_result_and_wait_complete_full_path()
    {
        RegisterSucceeding("demo");

        var started = await _tools.Start("demo", "{\"target\":\"web-1\"}", wait: false);
        Assert.True(started.Success);
        Assert.NotNull(started.JobId);
        Assert.Equal(JobStates.Running, started.State);

        var jobId = started.JobId!;
        await WaitUntilAsync(async () => (await _tools.Status(jobId)).State == JobStates.Succeeded);

        var status = await _tools.Status(jobId);
        Assert.True(status.Success);
        Assert.Equal(JobStates.Succeeded, status.State);
        Assert.Equal(1, status.ProgressDone);
        Assert.Equal(1, status.ProgressTotal);
        Assert.Equal(100, status.Progress);
        Assert.NotNull(status.RecentEvents);
        var progressEvent = Assert.Single(status.RecentEvents!, e => e.Kind == JobEventKinds.Progress);
        Assert.NotNull(progressEvent.Data);
        Assert.Null(progressEvent.Message);
        Assert.Equal("web-1", progressEvent.Server);
        Assert.Equal("done", progressEvent.Phase);
        Assert.Equal(1, progressEvent.Done);
        Assert.Equal(1, progressEvent.Total);
        Assert.Equal("succeeded", progressEvent.Status);
        Assert.Equal("web-1", progressEvent.Data!.Value.GetProperty("server").GetString());

        await WaitUntilAsync(async () =>
        {
            var current = await _tools.Events(jobId, 0);
            return current.Events.Any(e => e.Kind == JobEventKinds.Result);
        });
        var events = await _tools.Events(jobId, 0);
        Assert.Contains(events.Events, e => e.Kind == JobEventKinds.Progress);
        Assert.Contains(events.Events, e => e.Kind == JobEventKinds.Result);

        var result = await _tools.Result(jobId);
        Assert.True(result.Success);
        Assert.Equal(JobStates.Succeeded, result.State);
        Assert.Contains("ok", result.Result?.ToString());

        var summary = await _tools.Result(jobId, summary: true);
        Assert.True(summary.Success);
        Assert.Null(summary.Result);
        Assert.NotNull(summary.Summary);
        Assert.Equal("object", summary.Summary!.Kind);
        Assert.Contains("ok", summary.Summary.TopLevelKeys!);
        Assert.True(summary.ResultChars > 0);
        Assert.Contains("summary=false", summary.ResultHint);

        // wait=true：同步返回终态与结果
        var waited = await _tools.Start("demo", "{}", wait: true);
        Assert.True(waited.Success);
        Assert.Equal(JobStates.Succeeded, waited.State);
        Assert.Contains("ok", waited.Result?.ToString());
    }

    [Fact]
    public async Task Failed_runner_persists_error_state()
    {
        _manager.Register("fail_demo", "fail", (_, _) => Task.FromResult(
            JobManager.JobPrepared.Ready((_, _) => Task.FromResult(
                JobManager.JobOutcome.Fail("boom", "runner failed")))));

        var started = await _tools.Start("fail_demo", null, wait: true);
        Assert.True(started.Success);
        Assert.Equal(JobStates.Failed, started.State);
        Assert.Contains("runner failed", started.Error);

        var status = await _tools.Status(started.JobId!);
        Assert.Equal("boom", status.ErrorStatus);
        Assert.Contains("runner failed", status.ErrorText);

        var result = await _tools.Result(started.JobId!);
        Assert.False(result.Success);
        Assert.Equal(JobStates.Failed, result.State);
        Assert.Contains("runner failed", result.Error);
    }

    [Fact]
    public async Task Cancel_sets_flag_and_preserves_partial_events()
    {
        var startedRunner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.Register("cancel_demo", "cancel", (_, _) => Task.FromResult(
            JobManager.JobPrepared.Ready(async (ctx, ct) =>
            {
                await ctx.ReportProgressAsync(1, 2, "{\"server\":\"web-1\",\"done\":1,\"total\":2}", ct);
                startedRunner.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return JobManager.JobOutcome.Succeed();
            })));

        var started = await _tools.Start("cancel_demo", null, wait: false);
        Assert.True(started.Success);
        await startedRunner.Task;
        var jobId = started.JobId!;

        var cancel = await _tools.Cancel(jobId);
        Assert.True(cancel.Success);
        Assert.Equal("cancel_requested", cancel.Status);

        await WaitUntilAsync(async () => (await _tools.Status(jobId)).State == JobStates.Cancelled);
        var status = await _tools.Status(jobId);
        Assert.True(status.CancelRequested);
        Assert.True(status.Partial);

        var events = await _tools.Events(jobId, 0);
        Assert.Contains(events.Events, e => e.Kind == JobEventKinds.Progress);
    }

    [Fact]
    public async Task AsProgress_events_are_flushed_before_result()
    {
        _manager.Register("async_progress", "ordered progress", "topology_discover", (_, _) => Task.FromResult(
            JobManager.JobPrepared.Ready(async (ctx, ct) =>
            {
                var progress = ctx.AsProgress<int>(i => (i, 5, $"{{\"i\":{i}}}"));
                for (var i = 1; i <= 5; i++)
                    progress.Report(i);
                await Task.Yield();
                return JobManager.JobOutcome.Succeed(new { ok = true });
            })));

        var started = await _tools.Start("async_progress", null, wait: true);
        Assert.True(started.Success);

        var events = await _tools.Events(started.JobId!, 0);
        Assert.Equal(JobEventKinds.Result, events.Events[^1].Kind);
        Assert.True(events.Events.Count(e => e.Kind == JobEventKinds.Progress) >= 5);
    }

    [Fact]
    public async Task GetRunningJob_returns_current_job_progress()
    {
        var startedRunner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.Register("running_lookup", "lookup", (_, _) => Task.FromResult(
            JobManager.JobPrepared.Ready(async (ctx, ct) =>
            {
                await ctx.ReportProgressAsync(2, 5, "{\"done\":2,\"total\":5}", ct);
                startedRunner.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return JobManager.JobOutcome.Succeed();
            })));

        var started = await _tools.Start("running_lookup", null, wait: false);
        Assert.True(started.Success);
        await startedRunner.Task;

        var running = await _manager.GetRunningJobAsync("running_lookup");
        Assert.NotNull(running);
        Assert.Equal(started.JobId, running!.Id);
        Assert.Equal(2, running.ProgressDone);
        Assert.Equal(5, running.ProgressTotal);

        await _tools.Cancel(started.JobId!);
    }

    [Fact]
    public async Task Cross_process_cancel_flag_is_observed_by_runner()
    {
        var startedRunner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.Register("remote_cancel", "remote cancel", (_, _) => Task.FromResult(
            JobManager.JobPrepared.Ready(async (_, ct) =>
            {
                startedRunner.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return JobManager.JobOutcome.Succeed();
            })));

        var started = await _tools.Start("remote_cancel", null, wait: false);
        await startedRunner.Task;

        // 模拟另一个 MCP 进程只写共享 SQLite 的取消标记（不调用本进程 JobManager）
        var otherStore = new SqliteJobStore(_dbPath);
        await otherStore.SetCancelRequestedAsync(started.JobId!);

        await WaitUntilAsync(async () => (await _tools.Status(started.JobId!)).State == JobStates.Cancelled,
            TimeSpan.FromSeconds(10));
        Assert.Equal(JobStates.Cancelled, (await _tools.Status(started.JobId!)).State);
    }

    [Fact]
    public async Task Orphan_cleanup_marks_other_process_but_not_self()
    {
        await _store.SaveAsync(new JobState
        {
            Id = "job_orphan_other",
            Operation = "demo",
            OwnerUser = "alice",
            SessionId = "session-1",
            State = JobStates.Running,
            ProcessId = Environment.ProcessId + 1,
            HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });
        await _store.SaveAsync(new JobState
        {
            Id = "job_orphan_self",
            Operation = "demo",
            OwnerUser = "alice",
            SessionId = "session-1",
            State = JobStates.Running,
            ProcessId = Environment.ProcessId,
            HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });

        await _store.CleanupAsync(180, 24, 100, selfProcessId: Environment.ProcessId);

        Assert.Equal(JobStates.Orphaned, (await _store.GetAsync("job_orphan_other"))!.State);
        Assert.Equal(JobStates.Running, (await _store.GetAsync("job_orphan_self"))!.State);
    }

    [Fact]
    public async Task Cleanup_enforces_finished_job_limit_per_user()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.SaveAsync(new JobState
            {
                Id = $"job_finished_{i}",
                Operation = "demo",
                OwnerUser = "cleanup-user",
                SessionId = "session-1",
                State = JobStates.Succeeded,
                ProcessId = Environment.ProcessId,
                FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }

        await _store.CleanupAsync(180, 24, 2, selfProcessId: Environment.ProcessId);

        var remaining = await _store.ListAsync("cleanup-user", null, null, 100);
        Assert.Equal(2, remaining.Count);
    }

    [Fact]
    public async Task Visibility_is_owner_scoped()
    {
        RegisterSucceeding("visible_demo");
        var started = await _tools.Start("visible_demo", null, wait: true);
        Assert.True(started.Success);

        try
        {
            AuditContext.CurrentUserId = "bob";
            var status = await _tools.Status(started.JobId!);
            Assert.False(status.Success);
            Assert.Equal("job_not_found", status.Status);
        }
        finally
        {
            AuditContext.CurrentUserId = "alice";
        }
    }

    [Fact]
    public void Register_rejects_unknown_underlying_tool()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _manager.Register("bad_operation", "bad", "not_a_real_tool",
                (_, _) => Task.FromResult(JobManager.JobPrepared.Fail("unused", "unused"))));
    }

    [Fact]
    public void Job_start_route_fails_closed_without_operation_resolver()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["operation"] = JsonSerializer.SerializeToElement("topology_discover"),
            ["arguments"] = JsonSerializer.SerializeToElement("{\"serverIds\":\"web-1\"}")
        };

        var (descriptor, _) = ToolSecurityCatalog.ResolveForCall("job_start", args);
        Assert.Null(descriptor);
    }

    [Fact]
    public void Job_start_routes_arguments_to_underlying_operation_descriptor()
    {
        _manager.Register("topology_discover", "test", "topology_discover",
            (_, _) => Task.FromResult(JobManager.JobPrepared.Fail("unused", "unused")));
        _manager.Register("ssh_snapshot_refresh", "test", "ssh_snapshot_refresh",
            (_, _) => Task.FromResult(JobManager.JobPrepared.Fail("unused", "unused")));
        Func<string, ToolSecurityDescriptor?> resolver = op =>
            _manager.TryGetRequestDescriptor(op, out var descriptor) ? descriptor : null;

        var args = new Dictionary<string, JsonElement>
        {
            ["operation"] = JsonSerializer.SerializeToElement("topology_discover"),
            ["arguments"] = JsonSerializer.SerializeToElement("{\"serverIds\":\"web-1,db-1\"}")
        };

        var (descriptor, effectiveArgs) = ToolSecurityCatalog.ResolveForCall("job_start", args, resolver);

        Assert.NotNull(descriptor);
        Assert.Equal("topology_discover", descriptor!.Name);
        Assert.NotNull(effectiveArgs);
        Assert.Equal("web-1,db-1", effectiveArgs!["serverIds"].GetString());

        var snapshotArgs = new Dictionary<string, JsonElement>
        {
            ["operation"] = JsonSerializer.SerializeToElement("ssh_snapshot_refresh"),
            ["arguments"] = JsonSerializer.SerializeToElement("{\"serverId\":\"web-1\",\"force\":true}")
        };
        var (snapshotDescriptor, snapshotEffective) = ToolSecurityCatalog.ResolveForCall("job_start", snapshotArgs, resolver);
        Assert.NotNull(snapshotDescriptor);
        Assert.Equal("ssh_snapshot_refresh", snapshotDescriptor!.Name);
        Assert.Equal("web-1", snapshotEffective!["serverId"].GetString());

        var unknown = new Dictionary<string, JsonElement>
        {
            ["operation"] = JsonSerializer.SerializeToElement("not_registered"),
            ["arguments"] = JsonSerializer.SerializeToElement("{}")
        };
        var (none, _) = ToolSecurityCatalog.ResolveForCall("job_start", unknown, resolver);
        Assert.Null(none);
    }

    [Fact]
    public async Task Fleet_inventory_and_snapshot_operations_run()
    {
        var snapshotStore = new SnapshotStore(Path.Combine(_dir, "fleet-snapshots.db"));
        await snapshotStore.InitializeAsync();
        var fleet = new FleetJobOperations(new StubConfig(_config), snapshotStore,
            new FakeSnapshotService(), new FakeSsh());
        fleet.Register(_manager);

        var inventory = await _tools.Start("fleet_inventory",
            "{\"serverIds\":\"s1\"}", wait: true);
        Assert.True(inventory.Success, inventory.Error);
        Assert.True(inventory.Result?.TryGetProperty("servers", out _));

        var refresh = await _tools.Start("fleet_snapshot_refresh",
            "{\"serverIds\":\"s1\",\"force\":true}", wait: true);
        Assert.True(refresh.Success, refresh.Error);
        Assert.True(refresh.Result?.TryGetProperty("results", out _));
    }

    [Fact]
    public async Task Snapshot_refresh_job_operation_runs_and_reports_progress()
    {
        var snapshotStore = new SnapshotStore(Path.Combine(_dir, "snapshots.db"));
        await snapshotStore.InitializeAsync();
        var snapshotTools = new SnapshotTools(new StubConfig(_config), snapshotStore,
            new FakeSnapshotService(), new NoopAudit());
        snapshotTools.RegisterJobOperations(_manager);

        var started = await _tools.Start("ssh_snapshot_refresh",
            "{\"serverId\":\"s1\",\"force\":true}", wait: true);

        Assert.True(started.Success, started.Error);
        Assert.Equal(JobStates.Succeeded, started.State);
        Assert.Contains("succeeded", started.Result?.ToString());

        await WaitUntilAsync(async () =>
        {
            var events = await _tools.Events(started.JobId!, 0);
            return events.Events.Any(e => e.Kind == JobEventKinds.Phase) &&
                   events.Events.Any(e => e.Kind == JobEventKinds.Progress);
        });
    }

    private void RegisterSucceeding(string operation) =>
        _manager.Register(operation, "demo", (_, _) => Task.FromResult(
            JobManager.JobPrepared.Ready(async (ctx, ct) =>
            {
                await ctx.ReportProgressAsync(1, 1,
                    "{\"server\":\"web-1\",\"phase\":\"done\",\"done\":1,\"total\":1,\"status\":\"succeeded\"}", ct);
                return JobManager.JobOutcome.Succeed(new { ok = true });
            })));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail("等待 Job 状态超时");
    }

    private sealed class FakeSnapshotService : ISnapshotService
    {
        public Task<SnapshotRefreshResult> RefreshAsync(SshServerConfig server, SnapshotConfig options,
            CancellationToken ct = default, IProgress<SnapshotProgress>? progress = null) =>
            RefreshAsync(server, options, sectionNames: null, force: false, ct, progress);

        public Task<SnapshotRefreshResult> RefreshAsync(SshServerConfig server, SnapshotConfig options,
            IReadOnlyCollection<string>? sectionNames, bool force = false, CancellationToken ct = default,
            IProgress<SnapshotProgress>? progress = null)
        {
            progress?.Report(new SnapshotProgress("started", 0, 2, Status: "running"));
            progress?.Report(new SnapshotProgress("collector_start", 0, 2, "resource", "running"));
            progress?.Report(new SnapshotProgress("collector_done", 1, 2, "resource", "ok"));
            progress?.Report(new SnapshotProgress("completed", 2, 2, Status: "succeeded"));

            return Task.FromResult(new SnapshotRefreshResult
            {
                Status = "succeeded",
                SnapshotId = 1,
                Snapshot = new ServerSnapshot
                {
                    Id = 1,
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Status = SnapshotStatus.Succeeded,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    CompletedAt = DateTimeOffset.UtcNow.ToString("O"),
                    DataJson = "{\"sections\":{}}"
                }
            });
        }
    }

    private sealed class FakeSsh : ISshService
    {
        public Task<bool> TestConnectionAsync(SshServerConfig server, CancellationToken ct = default) => Task.FromResult(true);
        public Task<ConnectionProbeResult> ProbeConnectionAsync(SshServerConfig server, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionProbeResult { Success = true });
        public Task<CommandResult> ExecuteCommandAsync(SshServerConfig server, string command,
            CancellationToken ct = default, int timeoutSeconds = 60) => Task.FromResult(new CommandResult { Success = true });
        public Task<CommandResult> ExecuteWithSudoAsync(SshServerConfig server, string command,
            CancellationToken ct = default) => Task.FromResult(new CommandResult { Success = true });
        public Task<SshShellSession> OpenShellAsync(SshServerConfig server, string terminalType, uint columns,
            uint rows, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FileTransferResult> UploadFileAsync(SshServerConfig server, string localPath, string remotePath,
            IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FileTransferResult> DownloadFileAsync(SshServerConfig server, string remotePath, string localPath,
            IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RemoteFileListResult> ListRemoteFilesAsync(SshServerConfig server, string remotePath,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoteFileExistsAsync(SshServerConfig server, string remotePath,
            CancellationToken ct = default) => Task.FromResult(false);
        public Task<BatchTransferResult> DownloadBatchAsync(SshServerConfig server, IReadOnlyList<string> remotePaths,
            string localDirectory, bool recursive, int maxFiles, long maxTotalBytes,
            Func<string, bool>? overwriteResolver = null, CancellationToken ct = default,
            IProgress<BatchTransferFileProgress>? progress = null) => throw new NotSupportedException();
        public Task<BatchTransferResult> UploadBatchAsync(SshServerConfig server, IReadOnlyList<string> localPaths,
            string remoteDirectory, bool recursive, int maxFiles, long maxFileBytes, long maxTotalBytes,
            Func<string, bool>? overwriteResolver = null, CancellationToken ct = default,
            IProgress<BatchTransferFileProgress>? progress = null) => throw new NotSupportedException();
        public Task<CommandResult> ExecuteStreamingAsync(SshServerConfig server, string command,
            Action<string>? onStdout = null, Action<string>? onStderr = null, string? stdinText = null,
            CancellationToken ct = default, int timeoutSeconds = 120) => throw new NotSupportedException();
        public Task<RemoteCopyResult> CopyRemoteToRemoteAsync(RemoteCopyRequest request,
            IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubConfig(AppConfig config) : IConfigService
    {
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
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
}
