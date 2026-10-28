using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ToolProfileFilterTests
{
    [Fact]
    public void Diagnose_allows_readonly_remote_and_local_memory_but_not_writes()
    {
        Assert.True(ToolProfileFilter.AllowsRemote(new FakeTool("ssh_snapshot_get", "ssh", true), AgentToolProfile.Diagnose));
        // 非只读但只做探测的安全工具（写本地快照/拓扑缓存）在诊断 profile 可用
        Assert.True(ToolProfileFilter.AllowsRemote(new FakeTool("ssh_snapshot_refresh", "ssh", false), AgentToolProfile.Diagnose));
        Assert.True(ToolProfileFilter.AllowsRemote(new FakeTool("topology_discover", "topology", false), AgentToolProfile.Diagnose));
        Assert.False(ToolProfileFilter.AllowsRemote(new FakeTool("ssh_execute_command", "command", false), AgentToolProfile.Diagnose));
        Assert.True(ToolProfileFilter.AllowsLocal("ops_memory_write", AgentToolProfile.Diagnose));
        Assert.True(ToolProfileFilter.AllowsLocal("ops_diagnosis_write", AgentToolProfile.Diagnose));
        Assert.True(ToolProfileFilter.AllowsLocal("fleet_query", AgentToolProfile.Diagnose));
        Assert.False(ToolProfileFilter.AllowsLocal("ops_doc_write", AgentToolProfile.Diagnose));
        Assert.False(ToolProfileFilter.AllowsLocal("local_shell", AgentToolProfile.Diagnose));
    }

    [Fact]
    public void Assets_keeps_inventory_tools_and_docs_but_excludes_java_runtime()
    {
        Assert.True(ToolProfileFilter.AllowsRemote(new FakeTool("topology_query", "topology", true), AgentToolProfile.Assets));
        Assert.True(ToolProfileFilter.AllowsRemote(new FakeTool("ssh_snapshot_get", "ssh", true), AgentToolProfile.Assets));
        // 资产治理需要能刷新拓扑缓存；但不放行任何真实变更工具
        Assert.True(ToolProfileFilter.AllowsRemote(new FakeTool("topology_discover", "topology", false), AgentToolProfile.Assets));
        Assert.False(ToolProfileFilter.AllowsRemote(new FakeTool("docker_restart", "docker", false), AgentToolProfile.Assets));
        Assert.False(ToolProfileFilter.AllowsRemote(new FakeTool("service_restart", "service", false), AgentToolProfile.Assets));
        Assert.False(ToolProfileFilter.AllowsRemote(new FakeTool("java_diagnostics", "java", true), AgentToolProfile.Assets));
        Assert.True(ToolProfileFilter.AllowsLocal("ops_doc_write", AgentToolProfile.Assets));
        Assert.True(ToolProfileFilter.AllowsLocal("ops_script_register", AgentToolProfile.Assets));
        Assert.False(ToolProfileFilter.AllowsLocal("local_shell", AgentToolProfile.Assets));
    }

    [Fact]
    public void Chat_keeps_guide_and_readonly_local_tools_only()
    {
        Assert.True(ToolProfileFilter.AllowsRemote(new FakeTool("mcp_usage_guide", "guide", true), AgentToolProfile.Chat));
        Assert.False(ToolProfileFilter.AllowsRemote(new FakeTool("ssh_snapshot_get", "ssh", true), AgentToolProfile.Chat));
        // 安全探测白名单只放宽诊断/资产 profile，不进入问答 profile
        Assert.False(ToolProfileFilter.AllowsRemote(new FakeTool("ssh_snapshot_refresh", "ssh", false), AgentToolProfile.Chat));
        Assert.False(ToolProfileFilter.AllowsRemote(new FakeTool("topology_discover", "topology", false), AgentToolProfile.Chat));
        Assert.True(ToolProfileFilter.AllowsLocal("ops_doc_read", AgentToolProfile.Chat));
        Assert.True(ToolProfileFilter.AllowsLocal("ops_memory_list", AgentToolProfile.Chat));
        Assert.False(ToolProfileFilter.AllowsLocal("ops_memory_write", AgentToolProfile.Chat));
    }

    [Fact]
    public void Auto_and_change_keep_everything()
    {
        var write = new FakeTool("ssh_execute_command", "command", false);
        Assert.True(ToolProfileFilter.AllowsRemote(write, AgentToolProfile.Auto));
        Assert.True(ToolProfileFilter.AllowsRemote(write, AgentToolProfile.Change));
        Assert.True(ToolProfileFilter.AllowsLocal("local_shell", AgentToolProfile.Auto));
        Assert.True(ToolProfileFilter.AllowsLocal("local_shell", AgentToolProfile.Change));
    }

    [Fact]
    public void Display_maps_profiles()
    {
        Assert.Null(ToolProfileFilter.Display(AgentToolProfile.Auto));
        Assert.Equal("诊断/巡检", ToolProfileFilter.Display(AgentToolProfile.Diagnose));
        Assert.Equal("变更/发布", ToolProfileFilter.Display(AgentToolProfile.Change));
        Assert.Equal("资产治理", ToolProfileFilter.Display(AgentToolProfile.Assets));
        Assert.Equal("问答", ToolProfileFilter.Display(AgentToolProfile.Chat));
    }

    private sealed class FakeTool(string name, string? group, bool readOnly) : IAgentTool
    {
        public string Name { get; } = name;
        public AITool Tool => null!;
        public bool Destructive => false;
        public bool ReadOnly { get; } = readOnly;
        public string? ToolGroup { get; } = group;
        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct) =>
            Task.FromResult<object?>(null);
    }
}
