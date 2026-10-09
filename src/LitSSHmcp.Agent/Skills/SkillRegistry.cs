using System.Text;

namespace LitSSHmcp.Agent;

/// <summary>
/// 技能(markdown)加载：优先用用户配置目录，否则用内置技能目录（App 随包提供 litssh-mcp-ops-skill）。
/// 为节省每轮请求（含工具循环每一步）的固定 token：<b>小文件全文注入，大文件只注入索引</b>
/// （标题 + 适用描述 + 章节导读 + 按需读取方式），正文用 <c>skill_read(path="SKILL.md")</c> 按需读取。
/// <c>references/</c> 与 <c>*.template.md</c> 始终不注入，按需用 skill_read 读取。
/// </summary>
public static class SkillRegistry
{
    private const int MaxTotalChars = 8000;

    /// <summary>小于等于该长度的技能全文内联（对小技能保持旧行为），超过则"核心章节内联 + 其余索引"。</summary>
    private const int InlineFullTextLimit = 800;

    /// <summary>大技能内联的完整前置章节预算（字符）：按章节边界截取，保证开箱即用的核心流程常驻。</summary>
    private const int CoreInlineChars = 4000;

    private const int MaxDescriptionChars = 400;
    private const int MaxTocItems = 12;
    private const int MaxTocChars = 300;

    public static string Load(string? skillsDir, string? bundledSkillsDir = null)
    {
        var dir = ResolveDir(skillsDir, bundledSkillsDir);
        if (dir is null)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                     .Where(f => !f.EndsWith(".template.md", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(f => Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (sb.Length >= MaxTotalChars)
                break;

            try
            {
                var text = File.ReadAllText(file);
                if (text.Trim().Length <= InlineFullTextLimit)
                {
                    sb.AppendLine($"## 技能: {Path.GetFileNameWithoutExtension(file)}");
                    sb.AppendLine(text.Trim());
                }
                else
                {
                    AppendCoreAndIndex(sb, file, text);
                }
                sb.AppendLine();
            }
            catch
            {
                // 单个技能读取失败忽略
            }
        }

        var text2 = sb.ToString().Trim();
        return text2.Length <= MaxTotalChars ? text2 : text2[..MaxTotalChars] + "\n...(技能内容过长已截断)";
    }

    /// <summary>
    /// 大技能：内联**完整核心章节**（frontmatter 之后的 preamble + 按顺序尽量多的 <c>##</c> 整节，累计 ≤ <see cref="CoreInlineChars"/>），
    /// 其余章节只列标题并提示 <c>skill_read</c>；首个章节即超预算时退回纯索引。
    /// </summary>
    private static void AppendCoreAndIndex(StringBuilder sb, string file, string text)
    {
        var fileName = Path.GetFileName(file);
        var title = ExtractTitle(text);
        var desc = ExtractFrontmatterDescription(text);
        var (preamble, sections) = SplitSections(text);

        var body = new StringBuilder(preamble.TrimEnd());
        var inlined = 0;
        foreach (var section in sections)
        {
            if (body.Length + section.Length > CoreInlineChars)
                break;
            body.AppendLine().AppendLine(section.TrimEnd());
            inlined++;
        }

        // 连 preamble 都放不下：退回纯索引（保持极省 token 的行为）
        if (inlined == 0 && preamble.Trim().Length > CoreInlineChars)
        {
            AppendIndexEntry(sb, file, text);
            return;
        }

        var name = ExtractFrontmatterName(text);
        if (string.IsNullOrEmpty(name))
            name = Path.GetFileNameWithoutExtension(file);
        sb.AppendLine($"### {name}" + (string.IsNullOrEmpty(title) ? string.Empty : $"：{title}") + "（核心章节已内联）");
        if (desc.Length > 0)
            sb.AppendLine($"适用: {desc}");
        sb.AppendLine(body.ToString().TrimEnd());

        var remaining = sections.Skip(inlined).Select(GetSectionTitle).Where(t => t.Length > 0).ToList();
        if (remaining.Count > 0)
            sb.AppendLine($"（其余章节未展开: {string.Join(" / ", remaining.Take(MaxTocItems))}；需要时用 skill_read(path=\"{fileName}\") 读取全文）");
    }

    /// <summary>按 <c>##</c> 标题切分为 preamble + 各完整章节（跳过围栏代码块内的标题）。</summary>
    private static (string Preamble, List<string> Sections) SplitSections(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var preamble = new StringBuilder();
        var sections = new List<string>();
        var current = new StringBuilder();
        var inSection = false;
        var inFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                    sections.Add(current.ToString());
                inSection = true;
                current.Clear();
                current.AppendLine(line);
            }
            else if (inSection)
            {
                current.AppendLine(line);
            }
            else
            {
                preamble.AppendLine(line);
            }
        }

        if (inSection)
            sections.Add(current.ToString());
        return (preamble.ToString(), sections);
    }

    private static string GetSectionTitle(string section)
    {
        var first = section.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
        return first.StartsWith("## ", StringComparison.Ordinal) ? first[3..].Trim() : string.Empty;
    }

    /// <summary>大技能的索引条目：标题、适用场景（frontmatter description）、章节导读与按需读取方式。</summary>
    private static void AppendIndexEntry(StringBuilder sb, string file, string text)
    {
        var fileName = Path.GetFileName(file);
        var title = ExtractTitle(text);
        var desc = ExtractFrontmatterDescription(text);
        var toc = ExtractToc(text);

        sb.AppendLine($"### {Path.GetFileNameWithoutExtension(file)}" + (string.IsNullOrEmpty(title) ? string.Empty : $"：{title}"));
        if (desc.Length > 0)
            sb.AppendLine($"适用: {desc}");
        if (toc.Length > 0)
            sb.AppendLine($"章节: {toc}");
        sb.AppendLine($"（正文未注入以省 token；需要完整步骤时用 skill_read(path=\"{fileName}\") 读取全文）");
    }

    /// <summary>取 frontmatter 之后的第一个一级标题（无则空）。</summary>
    private static string ExtractTitle(string text)
    {
        var lines = text.Split('\n');
        var i = 0;
        if (lines.Length > 0 && lines[0].TrimEnd('\r') == "---")
        {
            i = 1;
            while (i < lines.Length && lines[i].TrimEnd('\r') != "---")
                i++;
            i++;
        }
        for (; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal))
                return line[2..].Trim();
        }
        return string.Empty;
    }

    /// <summary>取 YAML frontmatter 的 description 字段（截断到 <see cref="MaxDescriptionChars"/>）。</summary>
    private static string ExtractFrontmatterDescription(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "---")
            return string.Empty;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim() == "---")
                break;
            if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["description:".Length..].Trim().Trim('"', '\'');
                return value.Length <= MaxDescriptionChars ? value : value[..MaxDescriptionChars] + "…";
            }
        }
        return string.Empty;
    }

    /// <summary>取 YAML frontmatter 的 name 字段（无则空）。</summary>
    private static string ExtractFrontmatterName(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "---")
            return string.Empty;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim() == "---")
                break;
            if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                return line["name:".Length..].Trim().Trim('"', '\'');
        }
        return string.Empty;
    }

    /// <summary>取二级标题作为章节导读（跳过代码块内的 # 行）。</summary>
    private static string ExtractToc(string text)
    {
        var headers = new List<string>();
        var inFence = false;
        var chars = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
                continue;
            if (line.StartsWith("## ", StringComparison.Ordinal) && !line.StartsWith("### ", StringComparison.Ordinal))
            {
                var header = line[3..].Trim();
                headers.Add(header);
                chars += header.Length + 3;
                if (headers.Count >= MaxTocItems || chars >= MaxTocChars)
                    break;
            }
        }
        return string.Join(" / ", headers);
    }

    /// <summary>列出技能目录下可供 skill_read 按需读取的参考文件（相对路径，排除 SKILL.md）。</summary>
    public static IReadOnlyList<string> ListReferenceFiles(string dir, int max = 50)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                .Where(rel => !Path.GetFileName(rel).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static string? ResolveDir(string? skillsDir, string? bundledSkillsDir = null)
    {
        if (!string.IsNullOrWhiteSpace(skillsDir) && Directory.Exists(skillsDir))
            return skillsDir;
        if (!string.IsNullOrWhiteSpace(bundledSkillsDir) && Directory.Exists(bundledSkillsDir))
            return bundledSkillsDir;
        return null;
    }
}
