using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.Tests;

public class SshKnownHostsStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "litssh-kh-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Save_then_find_returns_entry()
    {
        var store = new FileSshKnownHostsStore(TempPath());
        store.Save(new SshKnownHost { Host = "10.0.0.1", Port = 22, KeyAlgorithm = "ssh-ed25519", FingerprintSha256 = "ABC" });

        var found = store.Find("10.0.0.1", 22);

        Assert.NotNull(found);
        Assert.Equal("ABC", found!.FingerprintSha256);
    }

    [Fact]
    public void Find_unknown_returns_null()
    {
        var store = new FileSshKnownHostsStore(TempPath());
        Assert.Null(store.Find("nope", 22));
    }

    [Fact]
    public void Persists_across_instances()
    {
        var path = TempPath();
        new FileSshKnownHostsStore(path).Save(new SshKnownHost { Host = "h", Port = 2222, FingerprintSha256 = "FP" });

        var reloaded = new FileSshKnownHostsStore(path).Find("h", 2222);

        Assert.NotNull(reloaded);
        Assert.Equal("FP", reloaded!.FingerprintSha256);
    }

    [Fact]
    public void Remove_deletes_entry()
    {
        var store = new FileSshKnownHostsStore(TempPath());
        store.Save(new SshKnownHost { Host = "h", Port = 22, FingerprintSha256 = "FP" });

        Assert.True(store.Remove("h", 22));
        Assert.Null(store.Find("h", 22));
        Assert.False(store.Remove("h", 22));
    }
}
