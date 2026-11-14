using System.IO;
using LitSSHmcp.App.Services.Backup;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class BackupItemCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-backup-catalog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Pet_assets_are_available_for_backup()
    {
        var petAssets = Path.Combine(_root, "pet-assets");
        Directory.CreateDirectory(Path.Combine(petAssets, "sample"));
        File.WriteAllText(Path.Combine(petAssets, "sample", "pet.json"), "{}");

        var item = Assert.Single(BackupItemCatalog.Build(new AppConfig(), _root),
            candidate => candidate.Id == "pet-assets");

        Assert.True(item.Exists);
        Assert.Equal(petAssets, item.Path);
        Assert.True(item.Size > 0);
        Assert.Contains("桌面宠物", item.Name);
    }

    [Fact]
    public void Terminal_logs_are_available_for_backup()
    {
        var terminalLogs = Path.Combine(_root, "terminal-logs");
        Directory.CreateDirectory(terminalLogs);
        File.WriteAllText(Path.Combine(terminalLogs, "session.cast"), "{}");

        var item = Assert.Single(BackupItemCatalog.Build(new AppConfig(), _root),
            candidate => candidate.Id == "terminal-logs");

        Assert.True(item.Exists);
        Assert.Equal(terminalLogs, item.Path);
        Assert.True(item.Size > 0);
        Assert.Contains("terminal-logs", item.Name);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
