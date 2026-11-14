using LitSSHmcp.McpServer.Services;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class SnapshotRequestTests
{
    [Fact]
    public void Explicit_sections_override_profile_and_are_deduplicated()
    {
        var sections = SnapshotRequest.ParseSections("quick", "portmap, portmap，systemd");
        Assert.Equal(new[] { "portmap", "systemd" }, sections);
    }

    [Fact]
    public void Quick_profile_uses_lightweight_sections()
    {
        var sections = SnapshotRequest.ParseSections("quick", null);
        Assert.Contains("portmap", sections!);
        Assert.Contains("security", sections!);
        Assert.True(SnapshotRequest.IsFull(" FULL "));
        Assert.False(SnapshotRequest.IsFull("brief"));
    }
}
