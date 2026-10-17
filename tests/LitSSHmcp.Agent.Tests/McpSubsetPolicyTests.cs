using System.Linq;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>MCP 开关两级模型（D8 / §6）启用层策略：解析、生效集合（收窄语义）、锁定规则。</summary>
public class McpSubsetPolicyTests
{
    private static AgentMcpServerConfig Server(string id, bool enabled, bool builtin = false)
        => new() { Id = id, Name = id, Enabled = enabled, Builtin = builtin, Transport = "stdio", Command = "x" };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("[")]
    [InlineData("[]")]
    public void Parse_subset_fails_safe_to_inherit(string? json)
    {
        // 空/坏数据一律回退 null = 继承全局默认集合（fail-safe：只会多给不会少给）
        Assert.Null(McpSubsetPolicy.ParseSubset(json));
    }

    [Fact]
    public void Parse_subset_builds_ordinal_set_and_drops_blank_ids()
    {
        var subset = McpSubsetPolicy.ParseSubset(@"[""litssh"",""ext1"","""",""litssh""]");

        Assert.NotNull(subset);
        Assert.Equal(2, subset!.Count);
        Assert.Contains("litssh", subset);
        Assert.Contains("ext1", subset);
    }

    [Fact]
    public void Serialize_subset_roundtrip_is_stable_and_sorted()
    {
        Assert.Equal(string.Empty, McpSubsetPolicy.SerializeSubset(null));
        Assert.Equal(string.Empty, McpSubsetPolicy.SerializeSubset(Array.Empty<string>()));

        var json = McpSubsetPolicy.SerializeSubset(new[] { "ext1", "litssh", "ext1", " " });
        Assert.Equal(@"[""ext1"",""litssh""]", json);
        Assert.Equal(2, McpSubsetPolicy.ParseSubset(json)!.Count);
    }

    [Fact]
    public void Effective_without_subset_is_globally_enabled_set()
    {
        var servers = new[] { Server("litssh", true, builtin: true), Server("ext1", true), Server("ext2", false) };

        var effective = McpSubsetPolicy.EffectiveServers(servers, null);

        Assert.Equal(new[] { "litssh", "ext1" }, effective.Select(s => s.Id));   // 配置顺序保持
    }

    [Fact]
    public void Effective_subset_narrows_but_never_widens()
    {
        var servers = new[] { Server("litssh", true, builtin: true), Server("ext1", true), Server("ext2", true) };
        var subset = McpSubsetPolicy.ParseSubset(@"[""ext1"",""ext-off""]");   // 含全局停用/已删除的 id

        var effective = McpSubsetPolicy.EffectiveServers(servers, subset);

        // 只保留"全局启用 ∧ 在子集内"：全局停用的 ext-off 与未选的 litssh/ext2 都出局（收窄语义）
        Assert.Equal(new[] { "ext1" }, effective.Select(s => s.Id));
    }

    [Fact]
    public void Effective_can_exclude_builtin_for_pure_chat_session()
    {
        // D8 例外：内置 LitSSH MCP 允许会话级整体关闭
        var servers = new[] { Server("litssh", true, builtin: true), Server("ext1", true) };
        var subset = McpSubsetPolicy.ParseSubset(@"[""ext1""]");

        var effective = McpSubsetPolicy.EffectiveServers(servers, subset);

        Assert.Equal(new[] { "ext1" }, effective.Select(s => s.Id));
        Assert.DoesNotContain(effective, s => s.Builtin);
    }

    [Theory]
    [InlineData(0, null, 0, false, true)]     // 草稿态（未落库、无目录、无轮次、空闲）：可编辑
    [InlineData(0, "", 0, false, true)]       // 未分配目录的草稿：可编辑
    [InlineData(1, null, 0, false, false)]    // 会话已落库（首条消息已发生）→ 锁定
    [InlineData(0, @"D:\ws", 0, false, false)]// 首条消息已分配工作目录 → 锁定
    [InlineData(0, null, 1, false, false)]    // 已有轮次 → 锁定
    [InlineData(0, null, 0, true, false)]     // 运行中 → 锁定
    public void Enable_layer_locks_after_first_message_or_while_running(
        long sessionId, string? workspaceDir, int turnCount, bool running, bool editable)
    {
        Assert.Equal(editable, McpSubsetPolicy.EnableLayerEditable(sessionId, workspaceDir, turnCount, running));
    }
}
