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

    [Fact]
    public void Build_adds_capability_boundary_when_groups_provided()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null,
            availableGroups: new[] { "mysql", "redis" });

        Assert.Contains("## 能力边界", prompt);
        Assert.Contains("MySQL", prompt);
        Assert.Contains("Redis", prompt);
        Assert.DoesNotContain("## 能力边界", SystemPromptBuilder.Build(null, null, null));
    }

    [Fact]
    public void Build_appends_agent_section_at_the_end()
    {
        var prompt = SystemPromptBuilder.Build(null, null, "USER_EXTRA",
            agentName: "运维专家", agentPrompt: "按 SRE 流程排障");

        // 智能体段必须在最末尾（切换智能体只失效尾部前缀缓存）
        Assert.EndsWith("按 SRE 流程排障", prompt);
        Assert.True(prompt.IndexOf("## 当前智能体", StringComparison.Ordinal) > prompt.IndexOf("## 用户附加要求", StringComparison.Ordinal));
        Assert.Contains("运维专家", prompt);
    }

    [Fact]
    public void Build_without_agent_omits_agent_section()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null);
        Assert.DoesNotContain("## 当前智能体", prompt);
    }

    [Fact]
    public void Build_agent_style_overrides_response_style_wording()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null, responseStyle: "concise",
            agentName: "数据库分析", agentPrompt: null, agentStyle: "detailed");

        // 方案 A：全局风格段保持原位，智能体覆盖写在尾部并声明覆盖
        Assert.Contains("篇幅：**默认尽量简短**", prompt);
        Assert.Contains("覆盖", prompt);
        Assert.Contains("可以详细展开", prompt);
        Assert.True(prompt.IndexOf("覆盖", StringComparison.Ordinal) > prompt.IndexOf("篇幅：**默认尽量简短**", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_includes_software_change_guardrail()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null);
        Assert.Contains("不要擅自安装/升级/卸载软件包", prompt);
    }

    [Fact]
    public void AppendAgent_appends_to_base_prompt()
    {
        var basePrompt = "TEMP_BASE_PROMPT";
        var withAgent = SystemPromptBuilder.AppendAgent(basePrompt, "通用聊天", "你是闲聊伙伴", null);

        Assert.StartsWith("TEMP_BASE_PROMPT", withAgent);
        Assert.Contains("## 当前智能体", withAgent);

        // 无智能体时原样返回（基础提示必须始终保留）
        Assert.Equal(basePrompt, SystemPromptBuilder.AppendAgent(basePrompt, null, null, null));
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

        var text = SkillRegistry.Load(_dir, null, null, new[] { "SKILL", "extra" });

        Assert.Contains("SKILL_BODY", text);
        Assert.Contains("EXTRA_BODY", text);
    }

    [Fact]
    public void Load_prefers_configured_dir_then_bundled()
    {
        var bundled = Path.Combine(_dir, "bundled");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "s.md"), "BUNDLED");

        Assert.Contains("BUNDLED", SkillRegistry.Load(null, bundled, null, new[] { "s" }));
        Assert.Equal(string.Empty, SkillRegistry.Load(null, null));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }
}
