using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 技能文件工具（本地、非 MCP）：只读，路径限制在技能目录内。
/// 技能索引（大技能的标题/描述/章节）已注入系统提示，SKILL.md 全文与 <c>references/</c> 等细节按需用 <c>skill(action=\"read\")</c> 读取，避免每次请求都携带整个技能目录。
/// </summary>
public static class SkillTools
{
    private const int MaxReadBytes = 100_000;

    public static IReadOnlyList<IAgentTool> Create(string skillsDir, IReadOnlySet<string>? availableGroups = null)
    {
        var root = Path.GetFullPath(skillsDir);

        var function = new LocalFunction(
            "skill",
            $"技能工具：action=match 按问题/资产类型匹配技能；action=list 列出参考文件；action=read 读取 SKILL.md 或 references 全文。技能根目录: {root}；路径相对该目录；开始排查前先 match，再 read。",
            """{"type":"object","properties":{"action":{"type":"string","enum":["match","list","read"],"description":"match=匹配技能(默认), list=列出参考文件, read=读取文件"},"query":{"type":"string","description":"match: 用户问题/告警/现象"},"assetTypes":{"type":"string","description":"match: 可选, 逗号分隔资产/技术类型, 如 ssh,ds,app,service,container,domain,cert,mysql,redis,nginx,docker,java"},"limit":{"type":"integer","description":"match: 最多返回条数, 默认3, 上限5"},"path":{"type":"string","description":"read: 相对技能目录的文件路径, 如 SKILL.md、references/ssh-workarounds.md"}},"required":[]}""",
            args =>
            {
                var action = (ArgumentReader.ReadString(args, "action") ?? "match").Trim().ToLowerInvariant();
                return action switch
                {
                    "match" => FormatMatches(SkillRegistry.Match(
                        root,
                        ArgumentReader.ReadString(args, "query"),
                        ArgumentReader.ReadString(args, "assetTypes"),
                        availableGroups,
                        ArgumentReader.ReadInt(args, "limit") ?? 3)),
                    "list" => ListFiles(root),
                    "read" => Read(root, ArgumentReader.ReadString(args, "path")),
                    _ => "错误: action 仅支持 match/list/read"
                };
            });

        return new IAgentTool[] { new LocalAgentTool(function, readOnly: true) };
    }

    private static string FormatMatches(IReadOnlyList<SkillMatch> matches)
    {
        if (matches.Count == 0)
            return "(未匹配到技能；可 skill(action=\"list\") 查看参考文件，或直接按工具取证。技能匹配仅供参考，不要凭记忆猜步骤。)";

        var sb = new StringBuilder();
        sb.AppendLine($"匹配到 {matches.Count} 个技能（按相关度）：");
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            sb.Append(i + 1).Append(". ").Append(m.Skill.Name)
                .Append(" | 路径: ").Append(m.Skill.MainPath)
                .Append(" | 域: ").Append(string.IsNullOrEmpty(m.Skill.DomainText) ? "未声明" : m.Skill.DomainText);
            if (!m.Skill.Applicable)
                sb.Append(" | **不可执行**: 未启用 ")
                    .Append(string.Join("、", m.Skill.DisabledDomains.Select(ToolGroups.Display)));
            sb.AppendLine();
            if (m.Reasons.Count > 0)
                sb.AppendLine("   匹配: " + string.Join("; ", m.Reasons));
            if (!string.IsNullOrWhiteSpace(m.Skill.Description))
                sb.AppendLine("   适用: " + m.Skill.Description);
        }
        sb.Append("用 skill(action=\"read\", path=...) 读取完整步骤；匹配仅供参考，执行前以工具实际结果为准。");
        return sb.ToString().Trim();
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
