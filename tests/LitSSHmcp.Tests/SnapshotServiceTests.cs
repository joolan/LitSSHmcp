using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Snapshot.Collectors;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>快照编排：单飞限流、成功/失败落库、致命错误中止、超时兜底。</summary>
public class SnapshotServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "litssh-snapsvc-" + Guid.NewGuid().ToString("N") + ".db");

    private static SshServerConfig Server() => new()
    {
        Id = "s1",
        Name = "web01",
        Host = "10.0.0.5",
        SudoType = SudoType.None
    };

    private async Task<(SnapshotService Service, SnapshotStore Store)> NewAsync(params ISnapshotCollector[] collectors)
    {
        var store = new SnapshotStore(_dbPath);
        await store.InitializeAsync();
        var service = new SnapshotService(store, new FakeSshService(), new FakeFilter(), collectors);
        return (service, store);
    }

    [Fact]
    public async Task Successful_refresh_persists_sections_and_events()
    {
        var (service, store) = await NewAsync(Ok("resource", 10));
        var result = await service.RefreshAsync(Server(), new SnapshotConfig());

        Assert.Equal("succeeded", result.Status);
        var latest = await store.GetLatestAsync("s1");
        Assert.Equal(SnapshotStatus.Succeeded, latest!.Status);
        using var doc = JsonDocument.Parse(latest.DataJson!);
        Assert.Equal("ok", doc.RootElement.GetProperty("sections").GetProperty("resource").GetProperty("status").GetString());

        var events = await store.GetEventsAsync(result.SnapshotId!.Value);
        Assert.Contains(events, e => e.Kind == "completed");
    }

    [Fact]
    public async Task Concurrent_refresh_returns_in_progress()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new TestCollector("resource", 10, async (_, _) =>
        {
            started.TrySetResult(true);
            await gate.Task;
            return CollectorResult.Ok(new { ok = true });
        });
        var (service, store) = await NewAsync(blocking);

        var first = service.RefreshAsync(Server(), new SnapshotConfig());
        await started.Task;

        var second = await service.RefreshAsync(Server(), new SnapshotConfig());
        Assert.Equal("snapshot_in_progress", second.Status);
        Assert.NotNull(second.SnapshotId);
        Assert.NotNull(await store.GetRunningAsync("s1"));

        gate.SetResult(true);
        Assert.Equal("succeeded", (await first).Status);
    }

    [Fact]
    public async Task Fatal_error_aborts_remaining_collectors_and_marks_failed()
    {
        var fatal = new TestCollector("resource", 10, (_, _) =>
            Task.FromResult(CollectorResult.Fail("connection refused", "network")));
        var second = Ok("systemd", 40);
        var (service, store) = await NewAsync(fatal, second);

        var result = await service.RefreshAsync(Server(), new SnapshotConfig());

        Assert.Equal("failed", result.Status);
        Assert.Equal(SnapshotStatus.Failed, (await store.GetLatestAsync("s1"))!.Status);
        var events = await store.GetEventsAsync(result.SnapshotId!.Value);
        Assert.Contains(events, e => e.Kind == "collector_failed" && e.Collector == "resource");
        Assert.Contains(events, e => e.Kind == "collector_skipped" && e.Collector == "systemd");
        Assert.Contains(events, e => e.Kind == "failed");
    }

    [Fact]
    public async Task Partial_failure_keeps_snapshot_succeeded_with_section_failed()
    {
        var good = Ok("resource", 10);
        var bad = new TestCollector("nginx_tls", 30, (_, _) =>
            Task.FromResult(CollectorResult.Fail("nginx -T 失败", null)));
        var (service, store) = await NewAsync(good, bad);

        var result = await service.RefreshAsync(Server(), new SnapshotConfig());

        Assert.Equal("succeeded", result.Status);
        using var doc = JsonDocument.Parse((await store.GetLatestAsync("s1"))!.DataJson!);
        var sections = doc.RootElement.GetProperty("sections");
        Assert.Equal("ok", sections.GetProperty("resource").GetProperty("status").GetString());
        Assert.Equal("failed", sections.GetProperty("nginx_tls").GetProperty("status").GetString());
    }

    [Fact]
    public async Task All_failures_mark_snapshot_failed()
    {
        var (service, store) = await NewAsync(
            new TestCollector("resource", 10, (_, _) => Task.FromResult(CollectorResult.Fail("boom", null))));

        var result = await service.RefreshAsync(Server(), new SnapshotConfig());

        Assert.Equal("failed", result.Status);
        Assert.Equal(SnapshotStatus.Failed, (await store.GetLatestAsync("s1"))!.Status);
    }

    [Fact]
    public async Task Timeout_is_recorded_as_failed()
    {
        var slow = new TestCollector("resource", 10, async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return CollectorResult.Ok(new { ok = true });
        });
        var (service, store) = await NewAsync(slow);

        var result = await service.RefreshAsync(Server(), new SnapshotConfig { TimeoutSeconds = 1 });

        Assert.Equal("failed", result.Status);
        Assert.Contains("超时", result.Error);
        Assert.Equal(SnapshotStatus.Failed, (await store.GetLatestAsync("s1"))!.Status);
    }

    [Fact]
    public void Datasource_matching_for_mysql_audit()
    {
        var server = new SshServerConfig { Id = "s1", Name = "web01", Host = "10.0.0.9" };
        var ips = new[] { "10.0.0.9", "172.17.0.1" };

        Assert.True(SnapshotService.MatchesServer(
            new DataSourceConfig { Type = "mysql", Host = "127.0.0.1", AccessMode = AccessMode.SshTunnel, TunnelServerId = "s1" }, server, ips));
        Assert.False(SnapshotService.MatchesServer(
            new DataSourceConfig { Type = "mysql", Host = "localhost", AccessMode = AccessMode.SshTunnel, TunnelServerId = "s2" }, server, ips));
        Assert.False(SnapshotService.MatchesServer(
            new DataSourceConfig { Type = "mysql", Host = "localhost", AccessMode = AccessMode.Direct }, server, ips));
        Assert.True(SnapshotService.MatchesServer(
            new DataSourceConfig { Type = "mysql", Host = "10.0.0.9", AccessMode = AccessMode.Direct }, server, ips));
        Assert.True(SnapshotService.MatchesServer(
            new DataSourceConfig { Type = "mysql", Host = "172.17.0.1", AccessMode = AccessMode.SshTunnel, TunnelServerId = "other" }, server, ips));
        Assert.False(SnapshotService.MatchesServer(
            new DataSourceConfig { Type = "mysql", Host = "192.168.1.5" }, server, ips));
    }

    [Fact]
    public async Task Mysql_probe_uses_matching_datasource_credentials()
    {
        var provider = new RecordingMysqlProvider();
        var config = new AppConfig
        {
            DataSources = new[]
            {
                new DataSourceConfig { Id = "d1", Type = "mysql", Host = "127.0.0.1", AccessMode = AccessMode.SshTunnel, TunnelServerId = "s1", Username = "root" }
            }
        };

        MysqlAuditProbeResult? observed = null;
        var collector = new TestCollector("security", 45, async (ctx, token) =>
        {
            observed = ctx.QueryMysqlAccountsAsync is null ? null : await ctx.QueryMysqlAccountsAsync(token);
            return CollectorResult.Ok(new Dictionary<string, object?> { ["ok"] = true });
        });

        var store = new SnapshotStore(_dbPath);
        await store.InitializeAsync();
        var service = new SnapshotService(store, new FakeSshService(), new FakeFilter(), new[] { collector },
            new FakeConfigService(config), provider);

        var result = await service.RefreshAsync(Server(), new SnapshotConfig());

        Assert.Equal("succeeded", result.Status);
        Assert.Equal("d1", provider.CalledId);
        Assert.NotNull(observed);
        Assert.False(observed!.Checked);
        Assert.Contains("权限", observed.Reason);
    }

    private static TestCollector Ok(string name, int order) =>
        new(name, order, (_, _) => Task.FromResult(CollectorResult.Ok(new Dictionary<string, object?> { ["v"] = 1 })));

    private sealed class TestCollector : ISnapshotCollector
    {
        private readonly Func<SnapshotContext, CancellationToken, Task<CollectorResult>> _behavior;

        public TestCollector(string name, int order, Func<SnapshotContext, CancellationToken, Task<CollectorResult>> behavior)
        {
            Name = name;
            Order = order;
            _behavior = behavior;
        }

        public string Name { get; }
        public int Order { get; }
        public Task<CollectorResult> CollectAsync(SnapshotContext context, CancellationToken ct) => _behavior(context, ct);
    }

    private sealed class FakeFilter : ICommandFilterService
    {
        public CommandFilterResult CheckCommand(string command) => CommandFilterResult.Allowed;
    }

    private sealed class FakeSshService : ISshService
    {
        public Task<bool> TestConnectionAsync(SshServerConfig server, CancellationToken ct = default) => Task.FromResult(true);

        public Task<ConnectionProbeResult> ProbeConnectionAsync(SshServerConfig server, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionProbeResult { Success = true });

        public Task<CommandResult> ExecuteCommandAsync(SshServerConfig server, string command, CancellationToken ct = default, int timeoutSeconds = 60) =>
            Task.FromResult(new CommandResult { Success = true });

        public Task<CommandResult> ExecuteWithSudoAsync(SshServerConfig server, string command, CancellationToken ct = default) =>
            Task.FromResult(new CommandResult { Success = true });

        public Task<FileTransferResult> UploadFileAsync(SshServerConfig server, string localPath, string remotePath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FileTransferResult> DownloadFileAsync(SshServerConfig server, string remotePath, string localPath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RemoteFileListResult> ListRemoteFilesAsync(SshServerConfig server, string remotePath, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConfigService : IConfigService
    {
        private readonly AppConfig _config;
        public FakeConfigService(AppConfig config) => _config = config;
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }

    /// <summary>模拟"数据源账号无 mysql.* 权限"：被调用即记录，并抛 access denied。</summary>
    private sealed class RecordingMysqlProvider : IMySqlConnectionProvider
    {
        public string? CalledId { get; private set; }
        public Task<IDatasourceSession> OpenAsync(DataSourceConfig ds, CancellationToken ct = default)
        {
            CalledId = ds.Id;
            throw new InvalidOperationException($"Access denied for user '{ds.Username}'@'%' to database 'mysql'");
        }
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }
}
