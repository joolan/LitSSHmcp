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
        Assert.DoesNotContain("## 技能目录", prompt);
    }

    [Fact]
    public void Build_injects_skills_root_section()
    {
        var prompt = SystemPromptBuilder.Build(null, "SKILL_INDEX", null, skillsRoot: @"C:\data\skills");

        Assert.Contains("## 技能目录", prompt);
        Assert.Contains(@"C:\data\skills", prompt);
        Assert.Contains("重新打开/新建会话", prompt);
        // 技能目录说明在技能索引之前（同为稳定前缀）
        Assert.True(prompt.IndexOf("## 技能目录", StringComparison.Ordinal)
                    < prompt.IndexOf("## 运维技能", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_skills_root_section_shown_even_without_skills()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null, skillsRoot: @"C:\data\skills");
        Assert.Contains("## 技能目录", prompt);
    }

    [Fact]
    public void Build_injects_workspace_doc_tools_section()
    {
        Assert.Contains("ops_doc_read", SystemPromptBuilder.Build(null, null, null));
        Assert.DoesNotContain("ops_doc_read", SystemPromptBuilder.Build(null, null, null, workspaceTools: false));
    }

    [Fact]
    public void Build_uses_merged_local_tool_names()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null, spillTools: true, memoryTools: true);
        Assert.Contains("ops_doc_write(mode=overwrite|append|patch)", prompt);
        Assert.Contains("ops_memory_write(action=save", prompt);
        Assert.Contains("tags=pitfall", prompt);
        Assert.Contains("spill(action=", prompt);
        Assert.DoesNotContain("ops_doc_append", prompt);
        Assert.DoesNotContain("ops_doc_patch", prompt);
        Assert.DoesNotContain("ops_memory_save", prompt);
        Assert.DoesNotContain("ops_memory_update", prompt);
        Assert.DoesNotContain("ops_memory_delete", prompt);
        Assert.DoesNotContain("spill_grep", prompt);
    }

    [Fact]
    public void Build_injects_session_workspace_notice_only_when_session_dir_assigned()
    {
        // 需求 D：会话分配独立目录 → 提示词注入"本会话工作目录 + 共享区 + shared/ 前缀"规则（软约束，代码层另强制）
        var prompt = SystemPromptBuilder.Build(null, null, null,
            sessionWorkspaceDir: @"C:\ws\jobs\local\s5", sharedWorkspaceDir: @"C:\ws");
        Assert.Contains("本会话工作目录", prompt);
        Assert.Contains(@"C:\ws\jobs\local\s5", prompt);
        Assert.Contains("共享工作区", prompt);
        Assert.Contains("shared/", prompt);
        Assert.Contains("禁止经 shared/ 访问 jobs/", prompt);

        // 脚本段：会话模式下说明脚本库恒在共享区
        var withScripts = SystemPromptBuilder.Build(null, null, null, scriptTools: true,
            sessionWorkspaceDir: @"C:\ws\jobs\local\s5", sharedWorkspaceDir: @"C:\ws");
        Assert.Contains("脚本库在共享区", withScripts);

        // 未分配会话（旧会话/现状行为）不注入会话目录段
        var legacy = SystemPromptBuilder.Build(null, null, null, scriptTools: true);
        Assert.DoesNotContain("本会话工作目录", legacy);
        Assert.DoesNotContain("脚本库在共享区", legacy);
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

    [Fact]
    public void Build_injects_worklog_section_and_uses_ops_doc_write()
    {
        Assert.DoesNotContain("## 工作日志", SystemPromptBuilder.Build(null, null, null));

        var enabled = SystemPromptBuilder.Build(null, null, null, workLog: true,
            workLogDir: @"C:\ws\worklogs",
            workLogPath: "shared/worklogs/2026/2026-10-28.md",
            workLogRoot: "shared/worklogs");
        Assert.Contains("## 工作日志", enabled);
        Assert.Contains(@"C:\ws\worklogs", enabled);
        Assert.Contains("ops_doc_write(mode=\"append\"", enabled);
        Assert.Contains("shared/worklogs/2026/2026-10-28.md", enabled);
        Assert.Contains("ops_doc_read(action=\"list\", path=\"shared/worklogs\")", enabled);
        Assert.DoesNotContain("ops_worklog_write", enabled);
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

        // 稳定目录测试隔离（不写真实 %APPDATA%\LitSSH\skills）
        var stable = Path.Combine(_dir, "stable");
        Assert.Contains("BUNDLED", SkillRegistry.Load(null, bundled, null, new[] { "s" }, stable));
        Assert.True(File.Exists(Path.Combine(stable, "s.md")));   // 首次使用把随包技能复制到稳定目录

        var emptyStable = Path.Combine(_dir, "stable-none");
        Assert.Equal(string.Empty, SkillRegistry.Load(null, null, null, null, emptyStable));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }
}
