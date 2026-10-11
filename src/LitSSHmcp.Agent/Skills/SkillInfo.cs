namespace LitSSHmcp.Agent;

using LitSSHmcp.Core.Models;

/// <summary>
/// 一条技能（技能目录项）的元数据：用于 AI 助手的技能选择器与按域裁剪。
/// <see cref="MainPath"/> 为相对技能根目录的路径（如 <c>nginx-5xx/SKILL.md</c>），可直接传给 <c>skill_read</c>。
/// </summary>
public sealed record SkillInfo(
    /// <summary>技能标识（目录名或单文件名，不含扩展名）。</summary>
    string Id,
    /// <summary>显示名（frontmatter name，缺省用 <see cref="Id"/>）。</summary>
    string Name,
    /// <summary>一级标题（无则空）。</summary>
    string Title,
    /// <summary>适用说明（frontmatter description）。</summary>
    string Description,
    /// <summary>相对技能根目录的主文件路径（skill_read 用）。</summary>
    string MainPath,
    /// <summary>涉及的工具能力域（frontmatter appliesTo ∪ 正文扫描）。</summary>
    IReadOnlyList<string> Domains,
    /// <summary>其中当前被停用的域。</summary>
    IReadOnlyList<string> DisabledDomains,
    /// <summary>是否可用（涉及域未被全部停用）。</summary>
    bool Applicable,
    /// <summary>主文件字节数。</summary>
    int SizeBytes)
{
    /// <summary>域标签（人类可读，逗号分隔）；无则空串。</summary>
    public string DomainText => Domains.Count == 0 ? string.Empty : string.Join("、", Domains.Select(ToolGroups.Display));
}
