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
            Assert.Contains("skill(action=", text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void List_reference_files_excludes_skill_md()
    {
        var files = SkillRegistry.ListReferenceFiles(_dir, activeSkillIds: new[] { "SKILL" });
        Assert.Contains("references/foo.md", files);
        Assert.DoesNotContain(files, f => Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void List_reference_files_filters_by_active_skill()
    {
        using var dir = new TempDir();
        WriteSkill(dir.Path, "a", "A");
        Directory.CreateDirectory(Path.Combine(dir.Path, "a", "references"));
        File.WriteAllText(Path.Combine(dir.Path, "a", "references", "a.md"), "A");
        WriteSkill(dir.Path, "b", "B");
        Directory.CreateDirectory(Path.Combine(dir.Path, "b", "references"));
        File.WriteAllText(Path.Combine(dir.Path, "b", "references", "b.md"), "B");

        // 索引模式：不注入任何参考文件
        Assert.Empty(SkillRegistry.ListReferenceFiles(dir.Path));

        var files = SkillRegistry.ListReferenceFiles(dir.Path, activeSkillIds: new[] { "a" });
        Assert.Contains("a/references/a.md", files);
        Assert.DoesNotContain("b/references/b.md", files);
    }

    [Fact]
    public void Load_caveats_partially_disabled_domains()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "SKILL.md"), "先 docker_query 看容器, 再 mysql_query 查库");
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
        File.WriteAllText(Path.Combine(dir.Path, "SKILL.md"), "先 docker_query 看容器");
        var enabled = new HashSet<string>(new[] { "mysql" }, StringComparer.OrdinalIgnoreCase);

        var text = SkillRegistry.Load(dir.Path, null, enabled);

        Assert.Equal(string.Empty, text);            // 技能涉及的域全被停用 → 整体跳过
    }

    [Fact]
    public void Load_without_groups_keeps_skills_uncaveated()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "SKILL.md"), "先 docker_query 看容器");

        var text = SkillRegistry.Load(dir.Path, null, null, new[] { "SKILL" });

        Assert.Contains("docker_query", text);
        Assert.DoesNotContain("未启用的能力域", text);
    }

    [Fact]
    public void List_enumerates_folder_skills_with_domains()
    {
        using var dir = new TempDir();
        var skillDir = Path.Combine(dir.Path, "nginx-5xx");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: nginx-5xx\ndescription: 排查 nginx 5xx\nappliesTo: [ssh, log]\n---\n# nginx\n... docker_query ...");

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
        File.WriteAllText(Path.Combine(dir.Path, "SKILL.md"), "正文包含 docker_query 的细节");
        var enabled = new HashSet<string>(new[] { "docker" }, StringComparer.OrdinalIgnoreCase);

        var text = SkillRegistry.Load(dir.Path, null, enabled);   // 未指定激活技能 → 索引模式

        Assert.Contains("skill(action=", text);       // 只给索引 + 读取提示
        Assert.DoesNotContain("细节", text);        // 不含正文
    }

    [Fact]
    public void Match_uses_triggers_symptoms_and_asset_types()
    {
        using var dir = new TempDir();
        WriteSkill(dir.Path, "disk-full", """
            ---
            name: disk-full
            description: 磁盘空间不足排查
            appliesTo: [ssh]
            triggers: [磁盘满, No space left, df 100%]
            symptoms: [disk_full]
            assets: [ssh]
            ---
            # 磁盘满
            """);
        WriteSkill(dir.Path, "nginx-5xx", """
            ---
            name: nginx-5xx
            description: nginx 5xx 排查
            appliesTo: [log]
            triggers: [502, 503, 504, 5xx]
            symptoms: [http_5xx]
            assets: [nginx]
            ---
            # nginx
            """);

        var disk = SkillRegistry.Match(dir.Path, "磁盘满了怎么处理", "ssh");
        Assert.Equal("disk-full", disk[0].Skill.Id);
        Assert.Contains(disk[0].Reasons, r => r.Contains("磁盘满"));
        Assert.Contains(disk[0].Reasons, r => r.Contains("资产 ssh"));

        var noSpace = SkillRegistry.Match(dir.Path, "No space left on device", "ssh");
        Assert.Equal("disk-full", noSpace[0].Skill.Id);
        Assert.Contains(noSpace[0].Reasons, r => r.Contains("No space left"));

        var nginx = SkillRegistry.Match(dir.Path, "nginx 502", "app");
        Assert.Equal("nginx-5xx", nginx[0].Skill.Id);

        Assert.Empty(SkillRegistry.Match(dir.Path, "完全无关的天气查询", null));
    }

    [Fact]
    public void Match_marks_skill_unavailable_when_required_domains_disabled()
    {
        using var dir = new TempDir();
        WriteSkill(dir.Path, "docker-container-down", """
            ---
            name: docker-container-down
            description: 容器起不来
            appliesTo: [docker]
            triggers: [容器起不来]
            symptoms: [container_down]
            assets: [container]
            ---
            # 容器
            """);
        var enabled = new HashSet<string>(new[] { "ssh" }, StringComparer.OrdinalIgnoreCase);

        var matches = SkillRegistry.Match(dir.Path, "容器起不来", "container", enabled);

        var match = Assert.Single(matches);
        Assert.False(match.Skill.Applicable);
        Assert.Contains(match.Reasons, r => r.Contains("未启用"));
    }

    [Fact]
    public async Task Skill_match_tool_returns_path_reasons_and_read_hint()
    {
        using var dir = new TempDir();
        WriteSkill(dir.Path, "disk-full", """
            ---
            name: disk-full
            description: 磁盘空间不足排查
            appliesTo: [ssh]
            triggers: [磁盘满, No space left]
            symptoms: [disk_full]
            assets: [ssh]
            ---
            # 磁盘满
            """);
        var tool = SkillTools.Create(dir.Path).Single(t => t.Name == "skill");

        var result = (await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["action"] = "match",
            ["query"] = "磁盘满了",
            ["assetTypes"] = "ssh"
        }, default))?.ToString() ?? string.Empty;

        Assert.Contains("disk-full", result);
        Assert.Contains("disk-full/SKILL.md", result);
        Assert.Contains("磁盘满", result);
        Assert.Contains("skill(action=", result);
    }

    [Fact]
    public void Builtin_skills_have_match_metadata_and_route_common_scenarios()
    {
        var root = FindSkillsRoot();
        Assert.NotNull(root);
        var skills = SkillRegistry.List(root!);
        Assert.Equal(14, skills.Count);
        Assert.All(skills, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));
        Assert.All(skills, s => Assert.True(s.Triggers.Count > 0, $"{s.Id} 缺少 triggers"));
        Assert.All(skills, s => Assert.True(s.Symptoms.Count > 0, $"{s.Id} 缺少 symptoms"));
        Assert.All(skills, s => Assert.True(s.AssetTypes.Count > 0, $"{s.Id} 缺少 assets"));

        var cases = new (string Query, string Expected)[]
        {
            ("磁盘满了怎么处理", "disk-full"),
            ("nginx 502", "nginx-5xx"),
            ("容器 Exited 反复重启", "docker-container-down"),
            ("证书过期", "ssl-cert-expiry"),
            ("MySQL 慢查询", "mysql-troubleshoot"),
            ("Redis 内存涨", "redis-troubleshoot"),
            ("Java Full GC", "java-gc-jvm"),
            ("服务 failed", "service-failure"),
            ("端口被占用", "port-process"),
            ("网络不通", "network-connectivity"),
            ("发布后回滚", "deploy-rollback"),
            ("日志里 Exception", "log-triage"),
            ("CPU 100%", "cpu-memory-high"),
            ("脚本上传执行", "script-deploy-verify")
        };
        foreach (var (query, expected) in cases)
            Assert.Contains(SkillRegistry.Match(root!, query).Take(3), m => m.Skill.Id == expected);
    }

    private static string? FindSkillsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "skills");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static void WriteSkill(string root, string id, string content)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
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
        var read = tools.Single(t => t.Name == "skill");

        var ok = await read.InvokeAsync(new Dictionary<string, object?> { ["action"] = "read", ["path"] = "references/foo.md" }, default);
        Assert.Equal("REFCONTENT", ok!.ToString());

        var escape = await read.InvokeAsync(new Dictionary<string, object?> { ["action"] = "read", ["path"] = "../secret.txt" }, default);
        Assert.Contains("非法路径", escape!.ToString());

        var missing = await read.InvokeAsync(new Dictionary<string, object?> { ["action"] = "read", ["path"] = "nope.md" }, default);
        Assert.Contains("文件不存在", missing!.ToString());
    }

    [Fact]
    public void Prompt_lists_skill_reference_files()
    {
        var prompt = SystemPromptBuilder.Build(null, "SKILL", null, skillFiles: new[] { "references/foo.md" });
        Assert.Contains("skill(action=", prompt);
        Assert.Contains("references/foo.md", prompt);
    }

    [Fact]
    public void Profile_name_is_rendered_in_prompt()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null, profileName: "诊断/巡检");
        Assert.Contains("当前会话模式", prompt);
        Assert.Contains("诊断/巡检", prompt);
    }

    [Fact]
    public void Fleet_change_guidance_is_rendered_when_tool_enabled()
    {
        var withChange = SystemPromptBuilder.Build(null, null, null, fleetChangeTools: true);
        var without = SystemPromptBuilder.Build(null, null, null);

        Assert.Contains("批量变更", withChange);
        Assert.Contains("fleet_change", withChange);
        Assert.Contains("canary", withChange);
        Assert.DoesNotContain("fleet_change", without);
    }

    [Fact]
    public void Diagnosis_contract_is_rendered_when_tool_enabled()
    {
        var withContract = SystemPromptBuilder.Build(null, null, null, diagnosisTools: true);
        var without = SystemPromptBuilder.Build(null, null, null);

        Assert.Contains("诊断/变更报告契约", withContract);
        Assert.Contains("ops_diagnosis_write", withContract);
        Assert.Contains("verification", withContract);
        Assert.DoesNotContain("ops_diagnosis_write", without);
    }

    [Fact]
    public void Fleet_tools_add_batch_query_guidance()
    {
        var withFleet = SystemPromptBuilder.Build(null, null, null, fleetTools: true);
        var without = SystemPromptBuilder.Build(null, null, null);

        Assert.Contains("fleet_query", withFleet);
        Assert.DoesNotContain("fleet_query", without);
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
        // 仅超长描述会被压缩；短描述（含 action 枚举/互斥提示）原样保留。
        var full = string.Concat(Enumerable.Repeat(
            "查询服务器整机快照。包含资源/端口/进程/服务/Docker/nginx证书/systemd健康/安全巡检等大量维度与详细说明文字。", 5));
        var shortText = ToolDescriptions.Compact(full);
        Assert.True(shortText.Length < full.Length);
        Assert.Contains("mcp_usage_guide", shortText);

        // 已经足够短则原样返回，不加提示
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
        Directory.CreateDirectory(Path.Combine(Bundled, "litssh-mcp-ops-skill", "references"));
        File.WriteAllText(Path.Combine(Bundled, "litssh-mcp-ops-skill", "SKILL.md"), "OPS");
        File.WriteAllText(Path.Combine(Bundled, "litssh-mcp-ops-skill", "references", "guide.md"), "OLD");
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
    public void Existing_stable_dir_keeps_user_skill_and_installs_missing_bundled_skill()
    {
        Directory.CreateDirectory(Path.Combine(Stable, "my-skill"));
        File.WriteAllText(Path.Combine(Stable, "my-skill", "SKILL.md"), "MINE");

        var dir = SkillRegistry.ResolveDir(null, Bundled, Stable);

        Assert.Equal(Stable, dir);
        Assert.Equal("MINE", File.ReadAllText(Path.Combine(Stable, "my-skill", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(Stable, "litssh-mcp-ops-skill", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(Stable, ".litssh-bundle.json")));
    }

    [Fact]
    public void Existing_bundled_files_are_never_overwritten()
    {
        SkillRegistry.ResolveDir(null, Bundled, Stable);
        var stableSkill = Path.Combine(Stable, "litssh-mcp-ops-skill", "SKILL.md");
        var stableRef = Path.Combine(Stable, "litssh-mcp-ops-skill", "references", "guide.md");
        File.WriteAllText(stableSkill, "USER EDITED");

        File.WriteAllText(Path.Combine(Bundled, "litssh-mcp-ops-skill", "SKILL.md"), "OPS2");
        File.WriteAllText(Path.Combine(Bundled, "litssh-mcp-ops-skill", "references", "guide.md"), "NEW");
        File.WriteAllText(Path.Combine(Bundled, "litssh-mcp-ops-skill", "references", "new.md"), "NEW FILE");

        SkillRegistry.ResolveDir(null, Bundled, Stable);

        // 简化策略：稳定目录已存在时只补“新技能目录”，不覆盖/不新增已有技能内的文件
        Assert.Equal("USER EDITED", File.ReadAllText(stableSkill));
        Assert.Equal("OLD", File.ReadAllText(stableRef));
        Assert.False(File.Exists(Path.Combine(Stable, "litssh-mcp-ops-skill", "references", "new.md")));
    }

    [Fact]
    public void Legacy_stable_dir_without_manifest_is_left_untouched()
    {
        Directory.CreateDirectory(Path.Combine(Stable, "litssh-mcp-ops-skill"));
        File.WriteAllText(Path.Combine(Stable, "litssh-mcp-ops-skill", "SKILL.md"), "OLD BUNDLE");

        SkillRegistry.ResolveDir(null, Bundled, Stable);

        Assert.Equal("OLD BUNDLE", File.ReadAllText(Path.Combine(Stable, "litssh-mcp-ops-skill", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(Stable, ".litssh-bundle.json")));
    }

    [Fact]
    public void Deleted_bundled_file_is_not_resurrected()
    {
        SkillRegistry.ResolveDir(null, Bundled, Stable);
        var stableRef = Path.Combine(Stable, "litssh-mcp-ops-skill", "references", "guide.md");
        File.Delete(stableRef);

        SkillRegistry.ResolveDir(null, Bundled, Stable);

        Assert.False(File.Exists(stableRef));
    }

    [Fact]
    public void New_bundled_skill_is_installed_on_next_resolve()
    {
        SkillRegistry.ResolveDir(null, Bundled, Stable);
        Directory.CreateDirectory(Path.Combine(Bundled, "new-skill"));
        File.WriteAllText(Path.Combine(Bundled, "new-skill", "SKILL.md"), "NEW SKILL");

        SkillRegistry.ResolveDir(null, Bundled, Stable);

        Assert.True(File.Exists(Path.Combine(Stable, "new-skill", "SKILL.md")));
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
