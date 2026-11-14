using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ToolGroupsViewModelTests
{
    [Fact]
    public void Tool_groups_view_exposes_every_registered_group()
    {
        var vm = new ToolGroupsViewModel(new StubConfig());

        Assert.Equal(ToolGroups.All, vm.Groups.Select(g => g.Key).ToArray());
        Assert.Contains(ToolGroups.Jobs, vm.Groups.Select(g => g.Key));
    }

    private sealed class StubConfig : IConfigService
    {
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(new AppConfig());
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }
}
