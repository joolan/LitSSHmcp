using System.IO;
using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>会话窗口「连接器」：读取 MCP 服务器清单并快速启用/停用（内置置顶、停用持久化、外部启用须校验）。</summary>
public class AgentConnectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-conn-" + Guid.NewGuid().ToString("N"));

    public AgentConnectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略 */ }
    }

    private (AgentViewModel Vm, string ConfigPath) CreateVm(AppConfig config)
    {
        var configPath = Path.Combine(_dir, "config.json");
        var configService = new ConfigService(configPath, new DpapiSecretProtector());
        configService.SaveConfigAsync(config).GetAwaiter().GetResult();
        var store = new ContextStore(Path.Combine(_dir, "agent.db"));
        store.InitializeAsync().GetAwaiter().GetResult();
        var vm = new AgentViewModel(configService, store, Path.Combine(_dir, "skills"));
        return (vm, configPath);
    }

    private static AppConfig ConfigWith(params AgentMcpServerConfig[] servers)
    {
        var config = new AppConfig();
        config.Agent.McpServers = servers;
        return config;
    }

    [Fact]
    public async Task Refresh_lists_builtin_first_then_external()
    {
        var external = new AgentMcpServerConfig { Id = "ext1", Name = "外部", Builtin = false, Enabled = false, Transport = "stdio", Command = "x" };
        var (vm, _) = CreateVm(ConfigWith(external));

        await vm.RefreshConnectorsAsync();

        Assert.Equal(2, vm.Connectors.Count);
        Assert.True(vm.Connectors[0].Builtin);
        Assert.Equal("litssh", vm.Connectors[0].Id);
        Assert.False(vm.Connectors[1].Builtin);
        Assert.Equal("ext1", vm.Connectors[1].Id);
        Assert.Equal("已停用", vm.Connectors[1].StatusText);
    }

    [Fact]
    public async Task Refresh_marks_enabled_without_runtime_as_not_connected()
    {
        var (vm, _) = CreateVm(ConfigWith());   // 默认内置启用；无运行时 → 未连接

        await vm.RefreshConnectorsAsync();

        var builtin = vm.Connectors[0];
        Assert.True(builtin.Builtin);
        Assert.True(builtin.IsEnabled);
        Assert.Equal("未连接", builtin.StatusText);
    }

    [Fact]
    public async Task Toggle_disable_builtin_persists_to_config()
    {
        var (vm, configPath) = CreateVm(ConfigWith());

        await vm.RefreshConnectorsAsync();
        var message = await vm.ToggleConnectorAsync(vm.Connectors[0]);

        Assert.Contains("已停用内置 MCP", message);
        var reloaded = await new ConfigService(configPath, new DpapiSecretProtector()).LoadConfigAsync();
        Assert.False(reloaded.Agent.McpServers.First(s => s.Builtin).Enabled);
    }

    [Fact]
    public async Task Toggle_enable_external_without_valid_server_stays_disabled()
    {
        var external = new AgentMcpServerConfig
        {
            Id = "ext1", Name = "坏服务器", Builtin = false, Enabled = false,
            Transport = "stdio", Command = "litssh-nonexistent-mcp-cmd-for-test"
        };
        var (vm, configPath) = CreateVm(ConfigWith(external));

        await vm.RefreshConnectorsAsync();
        var message = await vm.ToggleConnectorAsync(vm.Connectors[1]);

        Assert.Contains("失败", message);
        var reloaded = await new ConfigService(configPath, new DpapiSecretProtector()).LoadConfigAsync();
        var server = reloaded.Agent.McpServers.First(s => s.Id == "ext1");
        Assert.False(server.Enabled);                       // 校验失败保持停用
        Assert.False(string.IsNullOrEmpty(server.LastError)); // 错误已记录
    }

    [Fact]
    public async Task Toggle_rejects_unknown_id()
    {
        var (vm, _) = CreateVm(ConfigWith());

        await vm.RefreshConnectorsAsync();
        var ghost = new McpConnectorItem { Id = "ghost", Name = "不存在", Builtin = false };
        var message = await vm.ToggleConnectorAsync(ghost);

        Assert.Contains("找不到", message);
    }
}
