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
        var text = SkillRegistry.Load(_dir);
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

            var text = SkillRegistry.Load(dir);

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
