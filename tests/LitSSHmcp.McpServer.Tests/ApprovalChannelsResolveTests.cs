using LitSSHmcp.McpServer.Services;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class ApprovalChannelsResolveTests : IDisposable
{
    public ApprovalChannelsResolveTests()
    {
        Environment.SetEnvironmentVariable("LITSSH_APPROVAL_CHANNELS", null);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("LITSSH_APPROVAL_CHANNELS", null);

    [Fact]
    public void Env_override_wins_over_config()
    {
        Environment.SetEnvironmentVariable("LITSSH_APPROVAL_CHANNELS", "cli");
        Assert.Equal(new[] { "cli" }, ApprovalService.ResolveChannels(new[] { "desktop", "cli" }));
    }

    [Fact]
    public void Env_override_supports_multiple_and_whitespace()
    {
        Environment.SetEnvironmentVariable("LITSSH_APPROVAL_CHANNELS", " web , cli ");
        Assert.Equal(new[] { "web", "cli" }, ApprovalService.ResolveChannels(null));
    }

    [Fact]
    public void Empty_env_falls_back_to_config()
    {
        Environment.SetEnvironmentVariable("LITSSH_APPROVAL_CHANNELS", "  ");
        Assert.Equal(new[] { "desktop" }, ApprovalService.ResolveChannels(new[] { "desktop" }));
    }

    [Fact]
    public void No_env_and_no_config_defaults_desktop_cli()
    {
        Assert.Equal(new[] { "desktop", "cli" }, ApprovalService.ResolveChannels(null));
        Assert.Equal(new[] { "desktop", "cli" }, ApprovalService.ResolveChannels(Array.Empty<string>()));
    }
}
