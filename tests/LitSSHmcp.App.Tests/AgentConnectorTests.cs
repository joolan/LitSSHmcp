using System.IO;
using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 会话窗口「连接器」= D8 **启用层**入口：清单读取、勾选只写本会话子集（不回写全局配置）、
/// 全局停用的会话不能反向开启、未知 Id 拒绝。全局启停/校验属配置层（设置窗口，由其自身测试覆盖）。
/// </summary>
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
        Assert.False(vm.Connectors[1].GlobalEnabled);
        Assert.False(vm.Connectors[1].IsEnabled);
        Assert.Equal("全局已停用", vm.Connectors[1].StatusText);   // 配置层停用：会话不能反向开启
    }

    [Fact]
    public async Task Refresh_marks_enabled_without_runtime_as_not_connected()
    {
        var (vm, _) = CreateVm(ConfigWith());   // 默认内置启用；无运行时 → 未连接

        await vm.RefreshConnectorsAsync();

        var builtin = vm.Connectors[0];
        Assert.True(builtin.Builtin);
        Assert.True(builtin.GlobalEnabled);
        Assert.True(builtin.IsEnabled);         // 会话未设子集 = 继承全局默认集合
        Assert.Equal("未连接", builtin.StatusText);
    }

    [Fact]
    public async Task Toggle_disable_builtin_is_session_scoped_only()
    {
        var (vm, configPath) = CreateVm(ConfigWith());

        await vm.RefreshConnectorsAsync();
        var configBefore = File.ReadAllText(configPath);   // 读取可能触发迁移落盘：以刷新后的文件为基准
        var message = await vm.ToggleSessionConnectorAsync(vm.Connectors[0]);

        // 启用层：只写会话子集，全局配置（配置层）纹丝不动
        Assert.Contains("本会话已停用内置", message);
        Assert.Equal(configBefore, File.ReadAllText(configPath));

        // 再次刷新：勾选值与状态按本会话子集显示
        await vm.RefreshConnectorsAsync();
        Assert.False(vm.Connectors[0].IsEnabled);
        Assert.Equal("本会话已停用", vm.Connectors[0].StatusText);
    }

    [Fact]
    public async Task Toggle_back_to_global_set_returns_to_inherit()
    {
        var (vm, configPath) = CreateVm(ConfigWith());

        await vm.RefreshConnectorsAsync();
        var configBefore = File.ReadAllText(configPath);   // 读取可能触发迁移落盘：以刷新后的文件为基准
        await vm.ToggleSessionConnectorAsync(vm.Connectors[0]);   // 关
        await vm.RefreshConnectorsAsync();
        await vm.ToggleSessionConnectorAsync(vm.Connectors[0]);   // 开 → 与全局启用集合一致 → 回到继承态

        await vm.RefreshConnectorsAsync();
        Assert.True(vm.Connectors[0].IsEnabled);
        Assert.Equal("未连接", vm.Connectors[0].StatusText);      // 与"继承"路径同状态
        Assert.Equal(configBefore, File.ReadAllText(configPath)); // 配置层始终未动
    }

    [Fact]
    public async Task Toggle_rejects_globally_disabled_external()
    {
        var external = new AgentMcpServerConfig
        {
            Id = "ext1", Name = "未校验服务器", Builtin = false, Enabled = false,
            Transport = "stdio", Command = "litssh-nonexistent-mcp-cmd-for-test"
        };
        var (vm, configPath) = CreateVm(ConfigWith(external));

        await vm.RefreshConnectorsAsync();
        var message = await vm.ToggleSessionConnectorAsync(vm.Connectors[1]);

        // D8：会话启用层只能收窄全局集合，不能绕过设置窗口的校验去开启
        Assert.Contains("全局已停用", message);
        var reloaded = await new ConfigService(configPath, new DpapiSecretProtector()).LoadConfigAsync();
        var server = reloaded.Agent.McpServers.First(s => s.Id == "ext1");
        Assert.False(server.Enabled);
        Assert.Equal(string.Empty, server.LastError);   // 会话侧不触发校验，不写错误
    }

    [Fact]
    public async Task Toggle_rejects_unknown_id()
    {
        var (vm, _) = CreateVm(ConfigWith());

        await vm.RefreshConnectorsAsync();
        var ghost = new McpConnectorItem { Id = "ghost", Name = "不存在", Builtin = false, GlobalEnabled = true };
        var message = await vm.ToggleSessionConnectorAsync(ghost);

        Assert.Contains("找不到", message);
    }

    [Fact]
    public async Task Toggle_is_rejected_after_session_started()
    {
        // D8 锁定原则：会话已启动（工作目录已分配）后启用层不可再改
        var (vm, _) = CreateVm(ConfigWith());
        await vm.RefreshConnectorsAsync();
        Assert.False(vm.SessionSubsetLocked);   // 草稿态（未分配目录、无轮次、空闲）可编辑

        typeof(AgentViewModel)
            .GetField("_sessionWorkspaceDir", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(vm, @"C:\tmp\jobs\local\1");   // 模拟首条消息已分配工作目录

        Assert.True(vm.SessionSubsetLocked);
        var message = await vm.ToggleSessionConnectorAsync(vm.Connectors[0]);
        Assert.Contains("首条消息后锁定", message);
    }
}
