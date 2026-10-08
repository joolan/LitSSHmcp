using System.Text;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 技能参考文件工具（本地、非 MCP）：只读，路径限制在技能目录内。
/// 技能正文（SKILL.md）已注入系统提示，<c>references/</c> 等细节按需用 <c>skill_read</c> 读取，避免每次请求都携带整个技能目录。
/// </summary>
public static class SkillTools
{
    private const int MaxReadBytes = 100_000;

    public static IReadOnlyList<IAgentTool> Create(string skillsDir)
    {
        var root = Path.GetFullPath(skillsDir);

        var list = new LocalFunction(
            "skill_list",
            "列出可用的技能参考文件(相对技能目录的路径)",
            """{"type":"object","properties":{}}""",
            _ => ListFiles(root));

        var read = new LocalFunction(
            "skill_read",
            "读取技能参考文件(如 references/ssh-workarounds.md); path 相对技能目录, 只读",
            """{"type":"object","properties":{"path":{"type":"string","description":"相对技能目录的文件路径, 如 references/ssh-workarounds.md"}},"required":["path"]}""",
            args => Read(root, ArgumentReader.ReadString(args, "path")));

        return new IAgentTool[] { new LocalAgentTool(list, readOnly: true), new LocalAgentTool(read, readOnly: true) };
    }

    private static string ListFiles(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(rel => !Path.GetFileName(rel).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
        return files.Count == 0 ? "(技能目录下无参考文件)" : string.Join('\n', files);
    }

    private static string Read(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "(未指定 path)";

        string full;
        try { full = ResolvePath(root, path!); }
        catch (InvalidOperationException ex) { return $"(非法路径) {ex.Message}"; }

        if (!File.Exists(full))
            return $"(文件不存在) {path}";
        var info = new FileInfo(full);
        if (info.Length > MaxReadBytes)
            return $"(文件过大 {info.Length} 字节, 超过上限 {MaxReadBytes}, 未读取)";
        return File.ReadAllText(full, Encoding.UTF8);
    }

    /// <summary>把相对路径解析到技能目录内；拦截 <c>..</c> 与绝对路径逃逸。</summary>
    private static string ResolvePath(string root, string path)
    {
        var rel = path.Replace('\\', '/').TrimStart('/');
        if (rel.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("禁止 .. 越界");

        var full = Path.GetFullPath(Path.Combine(root, rel));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("越出技能目录");
        return full;
    }
}
