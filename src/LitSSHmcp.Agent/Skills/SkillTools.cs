using System.Text;
using LitSSHmcp.Core.Services.Security;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 技能文件工具（本地、非 MCP）：只读，路径限制在技能目录内。
/// 技能索引（大技能的标题/描述/章节）已注入系统提示，SKILL.md 全文与 <c>references/</c> 等细节按需用 <c>skill_read</c> 读取，避免每次请求都携带整个技能目录。
/// </summary>
public static class SkillTools
{
    private const int MaxReadBytes = 100_000;

    public static IReadOnlyList<IAgentTool> Create(string skillsDir)
    {
        var root = Path.GetFullPath(skillsDir);

        var list = new LocalFunction(
            "skill_list",
            $"列出可用的技能文件(相对技能目录的路径); 技能根目录: {root}",
            """{"type":"object","properties":{}}""",
            _ => ListFiles(root));

        var read = new LocalFunction(
            "skill_read",
            $"读取技能文件全文(如 SKILL.md 或 references/ssh-workarounds.md); path 相对技能目录({root}), 只读",
            """{"type":"object","properties":{"path":{"type":"string","description":"相对技能目录的文件路径, 如 SKILL.md、references/ssh-workarounds.md"}},"required":["path"]}""",
            args => Read(root, ArgumentReader.ReadString(args, "path")));

        return new IAgentTool[] { new LocalAgentTool(list, readOnly: true), new LocalAgentTool(read, readOnly: true) };
    }

    private static string ListFiles(string root)
    {
        // 不跟随 reparse point（junction/符号链接）：避免沿链接枚举到技能目录之外（§10.C.3）
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = Directory.EnumerateFiles(root, "*", options)
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

    /// <summary>
    /// 把相对路径解析到技能目录内；拦截 <c>..</c>、绝对路径逃逸与链接逃逸（评估报告 §10.C.3）。
    /// </summary>
    private static string ResolvePath(string root, string path)
    {
        var rel = path.Replace('\\', '/').TrimStart('/');
        if (rel.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("禁止 .. 越界");

        var full = Path.GetFullPath(Path.Combine(root, rel));
        var canonicalRoot = PathGuard.TryCanonicalize(root);
        var canonicalFull = PathGuard.TryCanonicalize(full);
        var underRoot = PathGuard.IsUnder(root, full);
        var isRootItself = canonicalRoot is not null && canonicalFull is not null &&
            string.Equals(canonicalRoot.TrimEnd(Path.DirectorySeparatorChar),
                canonicalFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        if (!underRoot || isRootItself || canonicalFull is null)
            throw new InvalidOperationException("越出技能目录");
        return full;
    }
}
