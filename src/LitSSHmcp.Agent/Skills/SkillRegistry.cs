using System.Text;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Agent;

/// <summary>
/// 技能(markdown)加载与目录。技能根目录下**每个子目录为一个技能**（含 <c>SKILL.md</c>，可带 <c>references/</c>），
/// 另兼容根目录下的单文件 <c>*.md</c>。选择器用 <see cref="List"/> 拿目录；注入系统提示时：
/// <list type="bullet">
///   <item>未指定激活技能 → 只注入**技能索引**（名称 + 适用描述 + 域 + <c>skill_read</c> 提示），极省 token；</item>
///   <item>指定了激活技能 → 只注入这些技能正文（小技能全文内联，大技能"核心章节内联 + 其余章节列标题"）。</item>
/// </list>
/// 并按当前可用能力域裁剪：涉及域全被停用的技能跳过，部分被停用的加提示。
/// </summary>
public static class SkillRegistry
{
    private const int MaxTotalChars = 8000;

    /// <summary>小于等于该长度的技能全文内联；超过则"核心章节内联 + 其余索引"。</summary>
    private const int InlineFullTextLimit = 800;

    /// <summary>大技能内联的完整前置章节预算（字符）。</summary>
    private const int CoreInlineChars = 4000;

    private const int MaxDescriptionChars = 400;
    private const int MaxTocItems = 12;
    private const int MaxTocChars = 300;

    private static readonly Regex ToolTokenRegex = new("[a-z][a-z0-9_]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// 生成注入系统提示的技能文本。<paramref name="activeSkillIds"/> 为空表示"索引模式"（只列索引）。
    /// </summary>
    public static string Load(string? skillsDir, string? bundledSkillsDir = null,
        IReadOnlySet<string>? availableGroups = null, IReadOnlyCollection<string>? activeSkillIds = null,
        string? stableDir = null)
    {
        var dir = ResolveDir(skillsDir, bundledSkillsDir, stableDir);
        if (dir is null)
            return string.Empty;

        var files = EnumerateSkillFiles(dir);
        var active = activeSkillIds is { Count: > 0 }
            ? new HashSet<string>(activeSkillIds, StringComparer.OrdinalIgnoreCase)
            : null;

        return active is null
            ? BuildIndex(files, availableGroups)
            : BuildActive(files, active, availableGroups);
    }

    /// <summary>列出技能目录（供 UI 技能选择器）。<paramref name="availableGroups"/> 为 null 表示不判定可用性。</summary>
    public static IReadOnlyList<SkillInfo> List(string root, IReadOnlySet<string>? availableGroups = null)
    {
        var result = new List<SkillInfo>();
        foreach (var file in EnumerateSkillFiles(root))
        {
            var domains = SkillDomains(file.Text);
            var disabled = availableGroups is null
                ? Array.Empty<string>()
                : domains.Where(g => !availableGroups.Contains(g)).ToArray();
            var applicable = availableGroups is null || domains.Length == 0 || domains.Any(g => availableGroups.Contains(g));
            var name = ExtractFrontmatterName(file.Text);
            if (string.IsNullOrEmpty(name))
                name = file.Id;

            result.Add(new SkillInfo(
                file.Id, name, ExtractTitle(file.Text), ExtractFrontmatterDescription(file.Text),
                file.MainPath, domains, disabled, applicable, Encoding.UTF8.GetByteCount(file.Text)));
        }
        return result.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- 注入构建 ----

    private static string BuildIndex(List<SkillFile> files, IReadOnlySet<string>? available)
    {
        var sb = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.Id, StringComparer.OrdinalIgnoreCase))
        {
            var domains = SkillDomains(file.Text);
            if (available is not null && domains.Length > 0 && !domains.Any(g => available.Contains(g)))
                continue;   // 涉及域全停用 → 索引也不列

            var name = ExtractFrontmatterName(file.Text);
            if (string.IsNullOrEmpty(name))
                name = file.Id;
            var desc = ExtractFrontmatterDescription(file.Text);

            sb.Append("- ").Append(name);
            if (!string.IsNullOrEmpty(desc))
                sb.Append("：").Append(desc);
            if (domains.Length > 0)
                sb.Append("（域: ").Append(string.Join(" / ", domains.Select(ToolGroups.Display))).Append("）");
            sb.Append($"; 需要完整步骤时用 skill_read(path=\"{file.MainPath}\") 读取");
            sb.AppendLine();
        }
        return Clamp(sb.ToString().Trim());
    }

    private static string BuildActive(List<SkillFile> files, HashSet<string> active, IReadOnlySet<string>? available)
    {
        var sb = new StringBuilder();
        foreach (var file in files.Where(f => active.Contains(f.Id)).OrderBy(f => f.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (sb.Length >= MaxTotalChars)
                break;

            var caveat = BuildCaveat(file.Text, available);
            if (caveat is not null)
                sb.AppendLine(caveat);

            if (file.Text.Trim().Length <= InlineFullTextLimit)
            {
                sb.AppendLine($"## 技能: {file.Id}");
                sb.AppendLine(file.Text.Trim());
            }
            else
            {
                AppendCoreAndIndex(sb, file, file.Text);
            }
            sb.AppendLine();
        }
        return Clamp(sb.ToString().Trim());
    }

    private static string Clamp(string text) =>
        text.Length <= MaxTotalChars ? text : text[..MaxTotalChars] + "\n...(技能内容过长已截断)";

    // ---- 技能文件枚举与元数据 ----

    private sealed record SkillFile(string Id, string MainPath, string Text);

    private static List<SkillFile> EnumerateSkillFiles(string root)
    {
        var list = new List<SkillFile>();
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(root))
            {
                var skill = Path.Combine(sub, "SKILL.md");
                if (!File.Exists(skill))
                    continue;
                var rel = Path.GetRelativePath(root, skill).Replace('\\', '/');
                list.Add(new SkillFile(Path.GetFileName(sub), rel, SafeRead(skill)));
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly))
            {
                if (file.EndsWith(".template.md", StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(new SkillFile(Path.GetFileNameWithoutExtension(file), Path.GetFileName(file), SafeRead(file)));
            }
        }
        catch
        {
            // 目录不可读 → 返回已收集的部分
        }
        return list;
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }

    /// <summary>技能涉及的能力域 = frontmatter <c>appliesTo</c>（规范化为分组键）∪ 正文扫描到的工具域。</summary>
    private static string[] SkillDomains(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in ExtractFrontmatterList(text, "appliesTo"))
        {
            var canonical = ToolGroups.All.FirstOrDefault(a => string.Equals(a, group, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null)
                set.Add(canonical);
        }
        foreach (var group in ReferencedGroups(text))
            set.Add(group);
        return set.ToArray();
    }

    private static string[] ExtractFrontmatterList(string text, string key)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "---")
            return Array.Empty<string>();

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim() == "---")
                break;
            if (line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(key.Length + 1)..].Trim().Trim('[', ']');
                return value.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }
        return Array.Empty<string>();
    }

    private static string? BuildCaveat(string text, IReadOnlySet<string>? available)
    {
        if (available is null)
            return null;

        var domains = SkillDomains(text);
        if (domains.Length == 0)
            return null;

        var disabled = domains.Where(g => !available.Contains(g))
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Select(ToolGroups.Display)
            .ToArray();
        if (disabled.Length == 0)
            return null;

        return $"（注意：本技能涉及当前未启用的能力域：{string.Join("、", disabled)}；相关步骤不可执行，遇到时应向用户说明并给出替代方案。）";
    }

    /// <summary>扫描文本中形如工具名的 token，映射到能力域（前缀即可判定，无需完整工具清单）。</summary>
    private static string[] ReferencedGroups(string text)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ToolTokenRegex.Matches(text))
        {
            var group = ToolGroups.GroupOf(match.Value);
            if (group is not null)
                groups.Add(group);
        }
        return groups.ToArray();
    }

    // ---- 正文内联（大技能：核心章节 + 其余索引） ----

    private static void AppendCoreAndIndex(StringBuilder sb, SkillFile file, string text)
    {
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

        if (inlined == 0 && preamble.Trim().Length > CoreInlineChars)
        {
            AppendIndexEntry(sb, file, text);
            return;
        }

        var name = ExtractFrontmatterName(text);
        if (string.IsNullOrEmpty(name))
            name = file.Id;
        sb.AppendLine($"### {name}" + (string.IsNullOrEmpty(title) ? string.Empty : $"：{title}") + "（核心章节已内联）");
        if (desc.Length > 0)
            sb.AppendLine($"适用: {desc}");
        sb.AppendLine(body.ToString().TrimEnd());

        var remaining = sections.Skip(inlined).Select(GetSectionTitle).Where(t => t.Length > 0).ToList();
        if (remaining.Count > 0)
            sb.AppendLine($"（其余章节未展开: {string.Join(" / ", remaining.Take(MaxTocItems))}；需要时用 skill_read(path=\"{file.MainPath}\") 读取全文）");
    }

    private static void AppendIndexEntry(StringBuilder sb, SkillFile file, string text)
    {
        var title = ExtractTitle(text);
        var desc = ExtractFrontmatterDescription(text);
        var toc = ExtractToc(text);
        var name = ExtractFrontmatterName(text);
        if (string.IsNullOrEmpty(name))
            name = file.Id;

        sb.AppendLine($"### {name}" + (string.IsNullOrEmpty(title) ? string.Empty : $"：{title}"));
        if (desc.Length > 0)
            sb.AppendLine($"适用: {desc}");
        if (toc.Length > 0)
            sb.AppendLine($"章节: {toc}");
        sb.AppendLine($"（正文未注入以省 token；需要完整步骤时用 skill_read(path=\"{file.MainPath}\") 读取全文）");
    }

    // ---- frontmatter / 章节解析 ----

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

    /// <summary>
    /// 稳定的技能根目录（<c>%APPDATA%\LitSSH\skills</c>）：跨重编译 / 升级保留，用户安装的技能放这里。
    /// </summary>
    public static string StableDir => Path.Combine(ConfigPaths.AppDataDir, "skills");

    /// <summary>
    /// 解析技能根目录，优先级：显式配置 → 稳定目录（不存在则由随包目录迁移而来）→ 随包 <c>skills/</c>。
    /// <paramref name="stableDir"/> 供测试隔离，默认 <see cref="StableDir"/>。
    /// </summary>
    public static string? ResolveDir(string? skillsDir, string? bundledSkillsDir = null, string? stableDir = null)
    {
        // 显式配置优先（与工作区目录一致：路径不存在则创建，便于直接在设置里指定新目录）
        if (!string.IsNullOrWhiteSpace(skillsDir))
        {
            try
            {
                Directory.CreateDirectory(skillsDir!);
                return skillsDir;
            }
            catch
            {
                // 配置的目录不可用（无权限/非法路径）→ 继续按默认回退
            }
        }

        var stable = EnsureStableDir(bundledSkillsDir, stableDir ?? StableDir);
        if (stable is not null)
            return stable;

        if (!string.IsNullOrWhiteSpace(bundledSkillsDir) && Directory.Exists(bundledSkillsDir))
            return bundledSkillsDir;
        return null;
    }

    /// <summary>
    /// 取稳定目录；首次使用时把随包技能复制过去（用户已装的技能不会随 bin 目录清理而丢失）。
    /// 两者都不可用时返回 null。
    /// </summary>
    private static string? EnsureStableDir(string? bundledSkillsDir, string stable)
    {
        try
        {
            if (Directory.Exists(stable))
                return stable;
            if (string.IsNullOrWhiteSpace(bundledSkillsDir) || !Directory.Exists(bundledSkillsDir))
                return null;
            if (Path.GetFullPath(bundledSkillsDir).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(stable.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return stable;

            CopyDirectory(bundledSkillsDir, stable);
            return Directory.Exists(stable) ? stable : null;
        }
        catch
        {
            return null;   // 迁移失败退回随包目录
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: false);
        }
    }
}
