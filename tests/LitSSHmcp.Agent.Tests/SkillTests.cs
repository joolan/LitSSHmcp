using System.Text;
using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class SkillTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-skill-" + Guid.NewGuid().ToString("N"));

    public SkillTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "references"));
        File.WriteAllText(Path.Combine(_dir, "SKILL.md"), "SKILLBODY");
        File.WriteAllText(Path.Combine(_dir, "OPS_ASSETS.template.md"), "TEMPLATE");
        File.WriteAllText(Path.Combine(_dir, "references", "foo.md"), "REFCONTENT");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Load_only_injects_top_level_skill_excluding_template()
    {
        var text = SkillRegistry.Load(_dir, null, null, new[] { "SKILL" });
        Assert.Contains("SKILLBODY", text);
        Assert.DoesNotContain("TEMPLATE", text);
        Assert.DoesNotContain("REFCONTENT", text);
    }

    [Fact]
    public void Load_inlines_core_sections_and_indexes_the_rest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-skill-big-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("name: big-skill");
            sb.AppendLine("description: 大技能测试");
            sb.AppendLine("---");
            sb.AppendLine("# 大技能");
            sb.AppendLine("核心前言");
            for (var i = 1; i <= 6; i++)
                sb.AppendLine($"## 章节{i}\n" + new string('x', 1500));
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), sb.ToString());

            var text = SkillRegistry.Load(dir, null, null, new[] { "SKILL" });

            Assert.Contains("核心前言", text);                 // preamble 内联
            Assert.Contains(new string('x', 1500), text);      // 核心章节正文内联
            Assert.Contains("核心章节已内联", text);
            Assert.Contains("其余章节未展开", text);            // 超预算章节仅列标题
            Assert.Contains("章节6", text);
            Assert.Contains("skill_read", text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void List_reference_files_excludes_skill_md()
    {
        var files = SkillRegistry.ListReferenceFiles(_dir);
        Assert.Contains("references/foo.md", files);
        Assert.DoesNotContain(files, f => Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_caveats_partially_disabled_domains()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "SKILL.md"), "先 docker_ps 看容器, 再 mysql_query 查库");
        var enabled = new HashSet<string>(new[] { "mysql" }, StringComparer.OrdinalIgnoreCase);

        var text = SkillRegistry.Load(dir.Path, null, enabled, new[] { "SKILL" });

        Assert.Contains("mysql_query", text);        // 技能整体保留
        Assert.Contains("未启用的能力域", text);      // 但加了提示
        Assert.Contains("容器", text);                // 提示里点名被停用的 docker 域
    }

    [Fact]
    public void Load_skips_skill_when_all_referenced_domains_disabled()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "SKILL.md"), "先 docker_ps 看容器");
        var enabled = new HashSet<string>(new[] { "mysql" }, StringComparer.OrdinalIgnoreCase);

        var text = SkillRegistry.Load(dir.Path, null, enabled);

        Assert.Equal(string.Empty, text);            // 技能涉及的域全被停用 → 整体跳过
    }

    [Fact]
    public void Load_without_groups_keeps_skills_uncaveated()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "SKILL.md"), "先 docker_ps 看容器");

        var text = SkillRegistry.Load(dir.Path, null, null, new[] { "SKILL" });

        Assert.Contains("docker_ps", text);
        Assert.DoesNotContain("未启用的能力域", text);
    }

    [Fact]
    public void List_enumerates_folder_skills_with_domains()
    {
        using var dir = new TempDir();
        var skillDir = Path.Combine(dir.Path, "nginx-5xx");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: nginx-5xx\ndescription: 排查 nginx 5xx\nappliesTo: [ssh, log]\n---\n# nginx\n... docker_ps ...");

        var enabled = new HashSet<string>(new[] { "ssh" }, StringComparer.OrdinalIgnoreCase);
        var list = SkillRegistry.List(dir.Path, enabled);

        var skill = Assert.Single(list);
        Assert.Equal("nginx-5xx", skill.Id);
        Assert.Equal("nginx-5xx", skill.Name);
        Assert.Contains("ssh", skill.Domains);
        Assert.Contains("log", skill.Domains);
        Assert.Contains("docker", skill.Domains);   // 由正文扫描补全
        Assert.Contains("log", skill.DisabledDomains);
        Assert.True(skill.Applicable);              // 至少 ssh 可用
    }

    [Fact]
    public void Load_index_mode_lists_skills_without_body()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "SKILL.md"), "正文包含 docker_ps 的细节");
        var enabled = new HashSet<string>(new[] { "docker" }, StringComparer.OrdinalIgnoreCase);

        var text = SkillRegistry.Load(dir.Path, null, enabled);   // 未指定激活技能 → 索引模式

        Assert.Contains("skill_read", text);       // 只给索引 + 读取提示
        Assert.DoesNotContain("细节", text);        // 不含正文
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "litssh-skill-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    [Fact]
    public async Task Skill_tools_read_and_block_escape()
    {
        var tools = SkillTools.Create(_dir);
        var read = tools.Single(t => t.Name == "skill_read");

        var ok = await read.InvokeAsync(new Dictionary<string, object?> { ["path"] = "references/foo.md" }, default);
        Assert.Equal("REFCONTENT", ok!.ToString());

        var escape = await read.InvokeAsync(new Dictionary<string, object?> { ["path"] = "../secret.txt" }, default);
        Assert.Contains("非法路径", escape!.ToString());

        var missing = await read.InvokeAsync(new Dictionary<string, object?> { ["path"] = "nope.md" }, default);
        Assert.Contains("文件不存在", missing!.ToString());
    }

    [Fact]
    public void Prompt_lists_skill_reference_files()
    {
        var prompt = SystemPromptBuilder.Build(null, "SKILL", null, skillFiles: new[] { "references/foo.md" });
        Assert.Contains("skill_read", prompt);
        Assert.Contains("references/foo.md", prompt);
    }

    [Fact]
    public void Response_style_shapes_output_guidance()
    {
        var concise = SystemPromptBuilder.Build(null, "S", null, responseStyle: "concise");
        Assert.Contains("结论先行", concise);
        Assert.Contains("只讲重点", concise);
        Assert.Contains("≤ 300 字", concise);

        var detailed = SystemPromptBuilder.Build(null, "S", null, responseStyle: "detailed");
        Assert.Contains("详细展开", detailed);
    }

    [Fact]
    public void Compact_tool_description_shortens_and_hints()
    {
        const string full = "查询服务器整机快照。包含资源/端口/进程/服务/Docker/nginx证书/systemd健康/安全巡检等大量维度与详细说明文字。";
        var shortText = ToolDescriptions.Compact(full);
        Assert.True(shortText.Length < full.Length);
        Assert.Contains("mcp_usage_guide", shortText);

        // 已经足够短、加提示反而更长时则不加
        Assert.Equal("短说明", ToolDescriptions.Compact("短说明"));
        Assert.Equal("见 mcp_usage_guide", ToolDescriptions.Compact(null));
    }
}

/// <summary>技能根目录解析：显式配置 → 稳定目录（首次由随包目录迁移）→ 随包目录。</summary>
public class SkillRootResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-skillroot-" + Guid.NewGuid().ToString("N"));
    private string Bundled => Path.Combine(_root, "bundled");
    private string Stable => Path.Combine(_root, "stable");

    public SkillRootResolutionTests()
    {
        Directory.CreateDirectory(Path.Combine(Bundled, "litssh-mcp-ops-skill"));
        File.WriteAllText(Path.Combine(Bundled, "litssh-mcp-ops-skill", "SKILL.md"), "OPS");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Configured_dir_wins_and_is_created()
    {
        var configured = Path.Combine(_root, "custom-skills");
        Assert.Equal(configured, SkillRegistry.ResolveDir(configured, Bundled, Stable));
        Assert.True(Directory.Exists(configured));
    }

    [Fact]
    public void Bundled_skills_are_migrated_to_stable_dir()
    {
        var dir = SkillRegistry.ResolveDir(null, Bundled, Stable);

        Assert.Equal(Stable, dir);
        Assert.True(File.Exists(Path.Combine(Stable, "litssh-mcp-ops-skill", "SKILL.md")));
        // 迁移不删除随包目录
        Assert.True(File.Exists(Path.Combine(Bundled, "litssh-mcp-ops-skill", "SKILL.md")));
    }

    [Fact]
    public void Existing_stable_dir_wins_and_is_not_overwritten()
    {
        Directory.CreateDirectory(Path.Combine(Stable, "my-skill"));
        File.WriteAllText(Path.Combine(Stable, "my-skill", "SKILL.md"), "MINE");

        var dir = SkillRegistry.ResolveDir(null, Bundled, Stable);

        Assert.Equal(Stable, dir);
        Assert.True(File.Exists(Path.Combine(Stable, "my-skill", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(Stable, "litssh-mcp-ops-skill")));   // 不覆盖已有稳定目录
    }

    [Fact]
    public void Returns_null_when_nothing_available()
    {
        Assert.Null(SkillRegistry.ResolveDir(null, null, Stable));
        Assert.Null(SkillRegistry.ResolveDir(null, Path.Combine(_root, "missing"), Stable));
    }

    [Fact]
    public void Skill_tools_descriptions_carry_root_path()
    {
        var tools = SkillTools.Create(Bundled);
        Assert.Contains(tools, t => t.Tool.Description is not null && t.Tool.Description.Contains(Bundled, StringComparison.OrdinalIgnoreCase));
    }
}
