using System.IO;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>快照历史窗口的详情渲染: 列表是轻量记录(不含 DataJson), 详情必须按 Id 取完整记录。</summary>
public class SnapshotHistoryViewModelTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "litssh-snapvm-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public async Task Detail_loads_full_record_by_id_not_lightweight_list_row()
    {
        var store = new SnapshotStore(_dbPath);
        await store.InitializeAsync();
        var id = (await store.BeginAsync("s1", "web01"))!.Value;
        await store.CompleteAsync(id,
            "{\"collectorVersion\":1,\"elevated\":true,\"sections\":{\"resource\":{\"status\":\"ok\",\"durationMs\":3,\"note\":null,\"data\":{\"hostname\":\"h\"}}}}",
            12, "sudo", 1);

        var vm = new SnapshotHistoryViewModel(store, new FakeConfigService())
        {
            SelectedServer = new SshServerConfig { Id = "s1", Name = "web01", Host = "10.0.0.5" }
        };

        await vm.LoadSnapshotsAsync();
        await vm.ShowSelectedAsync();

        Assert.Single(vm.Snapshots);
        Assert.NotEmpty(vm.Sections);                      // 采集概览非空(回归: 此前恒为空)
        Assert.Equal("resource", vm.Sections[0].Dimension);
        Assert.Equal("ok", vm.Sections[0].Status);
        Assert.Contains("resource", vm.RawJson);           // 原始数据非空
    }

    private sealed class FakeConfigService : IConfigService
    {
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(new AppConfig());
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }
}
