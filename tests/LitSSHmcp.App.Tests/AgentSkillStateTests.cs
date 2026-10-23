using System.IO;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AgentSkillStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-skills-" + Guid.NewGuid().ToString("N"));

    public AgentSkillStateTests()
    {
        var skillDir = Path.Combine(_root, "disk-triage");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# 磁盘排查\n\n使用 df 与 journalctl。");
    }

    [Fact]
    public void Reload_and_activate_builds_chips()
    {
        var state = new AgentSkillState(_ => { });
        state.Reload(_root, null);

        var skill = Assert.Single(state.AvailableSkills);
        Assert.True(state.HasAvailableSkills);
        Assert.False(state.HasActiveSkills);

        state.SetActiveIds(new[] { skill.Id, skill.Id });
        Assert.Single(state.ActiveIds);
        Assert.Single(state.ActiveSkills);
        Assert.True(state.HasActiveSkills);
        Assert.NotNull(state.ActiveIdsOrNull);

        state.RemoveActive(skill.Id);
        Assert.Empty(state.ActiveIds);
        Assert.Empty(state.ActiveSkills);
        Assert.Null(state.ActiveIdsOrNull);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
