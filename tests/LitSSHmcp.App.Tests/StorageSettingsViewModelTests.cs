using System.IO;
using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class StorageSettingsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-storage-" + Guid.NewGuid().ToString("N"));

    public StorageSettingsViewModelTests() => Directory.CreateDirectory(_root);

    private StorageSettingsViewModel CreateVm(AgentConfig? agent = null) =>
        new(new FakeConfigService(new AppConfig { Agent = agent ?? new AgentConfig() }), _root);

    [Fact]
    public async Task Refresh_counts_files_and_directories()
    {
        var ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(Path.Combine(ws, "jobs", "local"));
        Directory.CreateDirectory(Path.Combine(_root, "spills"));
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        Directory.CreateDirectory(Path.Combine(_root, "skills"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "config.json"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "agent.db"), new byte[20]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "audit.db"), new byte[30]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "spills", "x.txt"), new byte[40]);
        await File.WriteAllBytesAsync(Path.Combine(ws, "jobs", "local", "f"), new byte[50]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "logs", "l.log"), new byte[60]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "skills", "s.md"), new byte[70]);

        var vm = CreateVm(new AgentConfig { WorkspaceDir = ws });
        await vm.RefreshAsync();

        Assert.Equal(9, vm.Stats.Count);
        Assert.Equal(10, vm.Stats.Single(s => s.Name.Contains("config.json")).Bytes);
        Assert.Equal(20, vm.Stats.Single(s => s.Name.Contains("agent.db")).Bytes);
        Assert.Equal(40, vm.Stats.Single(s => s.Name.Contains("spill")).Bytes);
        Assert.Equal(50, vm.Stats.Single(s => s.Name.Contains("jobs")).Bytes);
        Assert.Equal(280, vm.Stats.Single(s => s.Name.Contains("数据目录合计")).Bytes);
    }

    [Fact]
    public async Task Prune_memory_history_uses_configured_data_dir()
    {
        var store = new AgentMemoryStore(Path.Combine(_root, "agent.db"));
        await store.InitializeAsync();
        var id = await store.AddStructuredAsync("task", "k", "会话", "v0", new[] { 1f, 0f }, "", "", 1);
        for (var i = 1; i <= 45; i++)
            await store.UpdateStructuredAsync(id, $"v{i}", new[] { 1f, 0f }, "", "", "active", 0);

        var vm = CreateVm();
        await vm.PruneMemoryHistoryAsync();

        Assert.Equal(AgentMemoryStore.DefaultHistoryKeep, (await store.ListVersionsAsync(id)).Count);
        Assert.Contains("已清理", vm.StatusMessage);
    }

    [Fact]
    public async Task Prune_spills_removes_files_older_than_retention()
    {
        var spillDir = Path.Combine(_root, "spills");
        Directory.CreateDirectory(spillDir);
        var oldFile = Path.Combine(spillDir, "old.txt");
        var newFile = Path.Combine(spillDir, "new.txt");
        await File.WriteAllTextAsync(oldFile, "old");
        await File.WriteAllTextAsync(newFile, "new");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10));

        var vm = CreateVm(new AgentConfig { SpillDir = spillDir, SpillRetentionDays = 7 });
        await vm.PruneSpillsAsync();

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
        Assert.Contains("已按 7 天", vm.StatusMessage);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void Format_bytes(long bytes, string expected) =>
        Assert.Equal(expected, StorageSettingsViewModel.FormatBytes(bytes));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private sealed class FakeConfigService(AppConfig config) : IConfigService
    {
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(config);
        public Task SaveConfigAsync(AppConfig value) => Task.CompletedTask;
        public string GetConfigPath() => "config.json";
    }
}
