using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class SystemPromptBuilderTests
{
    [Fact]
    public void Build_includes_all_sections()
    {
        var prompt = SystemPromptBuilder.Build("SERVER_RULES", "SKILL_TEXT", "USER_EXTRA");
        Assert.Contains("LitSSH 运维助手", prompt);
        Assert.Contains("SERVER_RULES", prompt);
        Assert.Contains("SKILL_TEXT", prompt);
        Assert.Contains("USER_EXTRA", prompt);
    }

    [Fact]
    public void Build_handles_nulls()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null);
        Assert.Contains("LitSSH 运维助手", prompt);
        Assert.DoesNotContain("## MCP 服务器约定", prompt);
        Assert.DoesNotContain("## 运维技能", prompt);
    }

    [Fact]
    public void Build_injects_workspace_doc_tools_section()
    {
        Assert.Contains("ops_doc_read", SystemPromptBuilder.Build(null, null, null));
        Assert.DoesNotContain("ops_doc_read", SystemPromptBuilder.Build(null, null, null, workspaceTools: false));
    }
}

public class SkillRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-skill-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_reads_markdown_files()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "SKILL.md"), "SKILL_BODY");
        File.WriteAllText(Path.Combine(_dir, "extra.md"), "EXTRA_BODY");

        var text = SkillRegistry.Load(_dir);

        Assert.Contains("SKILL_BODY", text);
        Assert.Contains("EXTRA_BODY", text);
    }

    [Fact]
    public void Load_prefers_configured_dir_then_bundled()
    {
        var bundled = Path.Combine(_dir, "bundled");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "s.md"), "BUNDLED");

        Assert.Contains("BUNDLED", SkillRegistry.Load(null, bundled));
        Assert.Equal(string.Empty, SkillRegistry.Load(null, null));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }
}
