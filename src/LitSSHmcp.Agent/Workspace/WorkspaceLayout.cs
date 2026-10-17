namespace LitSSHmcp.Agent;

/// <summary>
/// 两层工作区目录布局（评估报告需求 D / D6）：
/// 共享工作区（Documents\LitSSH：OPS_ASSETS 资产档案、scripts 脚本库、memory）
/// + 会话工作目录（jobs/&lt;owner&gt;/s&lt;sessionId&gt;/：任务产出、下载落地、附件、临时脚本）。
/// </summary>
public static class WorkspaceLayout
{
    /// <summary>会话目录统一放在共享工作区的 jobs/ 子树下。</summary>
    public const string JobsDirName = "jobs";

    /// <summary>
    /// 解析会话工作目录绝对路径：<c>&lt;sharedRoot&gt;/jobs/&lt;owner&gt;/s&lt;sessionId&gt;</c>。
    /// owner 段消毒（非法字符替换、防 <c>..</c> 与路径分隔符注入），sessionId 为库内自增数字。
    /// </summary>
    public static string ResolveSessionDir(string sharedRoot, string? ownerUserId, long sessionId)
    {
        var owner = SanitizeSegment(ownerUserId, fallback: "local");
        return Path.GetFullPath(Path.Combine(Path.GetFullPath(sharedRoot), JobsDirName, owner, SessionSegment(sessionId)));
    }

    /// <summary>会话目录名段：<c>s&lt;sessionId&gt;</c>。</summary>
    public static string SessionSegment(long sessionId) => "s" + sessionId;

    /// <summary>
    /// 消毒目录名段：替换文件名非法字符与路径分隔符，去掉首尾点/空格，拒绝 "."/".."，
    /// 长度截断；空结果回退 <paramref name="fallback"/>。
    /// </summary>
    public static string SanitizeSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch =>
            invalid.Contains(ch) || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : ch).ToArray();
        var seg = new string(chars).Trim().Trim('.').Trim();

        if (seg.Length == 0 || seg is "." or "..")
            return fallback;
        if (seg.Length > 64)
            seg = seg[..64].Trim();
        return seg.Length == 0 ? fallback : seg;
    }
}
