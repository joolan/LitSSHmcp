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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
