using LitSSHmcp.App.Services;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AppServicesTests
{
    [Fact]
    public void Composition_root_reuses_config_and_port_forward()
    {
        var services = new AppServices();
        Assert.Same(services.Config, services.Config);
        Assert.Same(services.PortForward, services.PortForward);
        Assert.NotNull(services.Dialogs);
        Assert.NotNull(services.Windows);
        Assert.NotNull(services.CreateSshService());
        Assert.NotNull(services.CreateAuditLog());
        Assert.NotNull(services.CreateSnapshotStore());
        Assert.NotNull(services.CreateTopologyStore());
        Assert.NotNull(services.CreateAgentContextStore());
        Assert.False(string.IsNullOrWhiteSpace(services.BundledSkillsDir));
    }
}
