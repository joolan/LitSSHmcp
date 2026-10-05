using System.Text;

namespace LitSSHmcp.Agent;

/// <summary>
/// 技能(markdown)加载：优先用用户配置目录，否则用内置技能目录（App 随包提供 litssh-mcp-ops-skill）。
/// **默认只注入顶层 SKILL.md**（不含 <c>references/</c> 与 <c>*.template.md</c>），其余参考文件按需用 skill_read 读取，避免每轮请求都携带整个技能目录。
/// </summary>
public static class SkillRegistry
{
    private const int MaxTotalChars = 60000;

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
                sb.AppendLine($"## 技能: {Path.GetFileNameWithoutExtension(file)}");
                sb.AppendLine(File.ReadAllText(file).Trim());
                sb.AppendLine();
            }
            catch
            {
                // 单个技能读取失败忽略
            }
        }

        var text = sb.ToString().Trim();
        return text.Length <= MaxTotalChars ? text : text[..MaxTotalChars] + "\n...(技能内容过长已截断)";
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
