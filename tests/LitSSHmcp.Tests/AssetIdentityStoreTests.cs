using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

public class AssetIdentityStoreTests
{
    [Fact]
    public async Task Store_upserts_and_reads_identities()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"litssh-identity-{Guid.NewGuid():N}.db");
        try
        {
            var store = new AssetIdentityStore(dbPath);
            await store.InitializeAsync();

            Assert.True(await store.TryUpsertAsync(new AssetIdentityRecord
            {
                AssetId = "s1",
                AssetType = "ssh",
                IdentityKind = AssetIdentityKinds.SshMachineId,
                IdentityValue = "machine-1",
                Source = "probe:ssh",
                Confidence = 0.98
            }));

            Assert.True(await store.TryUpsertAsync(new AssetIdentityRecord
            {
                AssetId = "s1",
                AssetType = "ssh",
                IdentityKind = AssetIdentityKinds.SshHostname,
                IdentityValue = "web1",
                Source = "probe:ssh",
                Confidence = 0.5
            }));

            Assert.False(await store.TryUpsertAsync(new AssetIdentityRecord
            {
                AssetId = "s2",
                AssetType = "ssh",
                IdentityKind = AssetIdentityKinds.SshMachineId,
                IdentityValue = "machine-1",
                Source = "probe:ssh",
                Confidence = 0.98
            }));

            var all = await store.GetAllAsync();
            Assert.Equal(2, all.Count);
            Assert.Contains(all, r => r.AssetId == "s1" && r.IdentityKind == AssetIdentityKinds.SshMachineId);
            Assert.Contains(all, r => r.AssetId == "s1" && r.IdentityKind == AssetIdentityKinds.SshHostname);

            var byAsset = await store.GetByAssetAsync("s1");
            Assert.Equal(2, byAsset.Count);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}
