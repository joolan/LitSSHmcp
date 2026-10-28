using LitSSHmcp.Agent;
using LitSSHmcp.Agent.Sessions;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class TimelineToolsTests : IDisposable
{
    private readonly string _contextPath = Path.Combine(Path.GetTempPath(), $"litssh-timeline-ctx-{Guid.NewGuid():N}.db");
    private readonly string _memoryPath = Path.Combine(Path.GetTempPath(), $"litssh-timeline-mem-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var path in new[] { _contextPath, _memoryPath })
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch { /* 忽略 */ }
            }
        }
    }

    [Fact]
    public async Task Session_timeline_contains_user_tool_and_assistant_steps()
    {
        var context = new ContextStore(_contextPath);
        await context.InitializeAsync();
        var sessionId = await context.CreateSessionAsync("测试");
        await context.AppendMessageAsync(sessionId, "user", "磁盘满怎么办");
        await context.AppendMessageAsync(sessionId, "tool",
            """{"Tool":"ssh_snapshot_get","Args":"{\"serverId\":\"s1\"}","Result":"df 100%","Ok":true,"Ms":12,"IsInfo":false}""");
        await context.AppendMessageAsync(sessionId, "assistant", "已清理 journal 并恢复");
        var tool = TimelineTools.Create(null, context);

        var text = (await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId.ToString()
        }, default))?.ToString() ?? string.Empty;

        Assert.Contains("## 时间线", text);
        Assert.Contains("用户: 磁盘满怎么办", text);
        Assert.Contains("工具 ssh_snapshot_get 成功", text);
        Assert.Contains("df 100%", text);
        Assert.Contains("助手: 已清理 journal 并恢复", text);
    }

    [Fact]
    public async Task Incident_timeline_links_incident_to_session()
    {
        var context = new ContextStore(_contextPath);
        await context.InitializeAsync();
        var sessionId = await context.CreateSessionAsync("测试");
        await context.AppendMessageAsync(sessionId, "user", "证书过期");
        await context.AppendMessageAsync(sessionId, "assistant", "已续期");

        var memory = new AgentMemoryStore(_memoryPath);
        await memory.InitializeAsync();
        var incidentId = await memory.AddIncidentAsync("证书续期", "证书过期", "ssh:s1", "未续期",
            "renew", "恢复", "cert:1", 0.9, "resolved", $"agent-{sessionId}");

        var tool = TimelineTools.Create(memory, context);
        var text = (await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["incidentId"] = incidentId
        }, default))?.ToString() ?? string.Empty;

        Assert.Contains($"## 事件 #{incidentId}", text);
        Assert.Contains("- 根因: 未续期", text);
        Assert.Contains("## 时间线", text);
        Assert.Contains("用户: 证书过期", text);
        Assert.Contains("助手: 已续期", text);
    }

    [Fact]
    public async Task Missing_incident_returns_clear_message()
    {
        var tool = TimelineTools.Create(new AgentMemoryStore(_memoryPath), new ContextStore(_contextPath));

        var text = (await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["incidentId"] = 999999
        }, default))?.ToString() ?? string.Empty;

        Assert.Contains("不存在", text);
    }
}
