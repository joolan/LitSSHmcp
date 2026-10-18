using LitSSHmcp.Core.Services.Security;

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

    /// <summary>
    /// 会话目录名段：<c>s&lt;sessionId&gt;</c>。
    /// </summary>
    public static string SessionSegment(long sessionId) => "s" + sessionId;

    /// <summary>
    /// 解析直接挂在给定 jobs 根下的会话目录：<c>&lt;jobsRoot&gt;/&lt;owner&gt;/s&lt;sessionId&gt;</c>。
    /// Web 固定根用此口径（评估报告 §8.3：<c>%APPDATA%\LitSSH\jobs\&lt;user&gt;\s&lt;id&gt;</c>）；
    /// 桌面共享工作区用 <see cref="ResolveSessionDir"/>（其下再套 jobs 子树）。
    /// </summary>
    public static string ResolveSessionDirInJobsRoot(string jobsRoot, string? ownerUserId, long sessionId)
    {
        var owner = SanitizeSegment(ownerUserId, fallback: "local");
        return Path.GetFullPath(Path.Combine(Path.GetFullPath(jobsRoot), owner, SessionSegment(sessionId)));
    }

    /// <summary>会话目录名 → 会话 Id（严格 <c>s&lt;正整数&gt;</c>）。</summary>
    public static bool TryParseSessionId(string? dirName, out long sessionId)
    {
        sessionId = 0;
        if (string.IsNullOrEmpty(dirName) || dirName.Length < 2 || dirName[0] != 's')
            return false;
        return long.TryParse(dirName[1..], System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out sessionId)
               && sessionId > 0;
    }

    /// <summary>
    /// 删除直接挂在 jobs 根下的会话目录（Web 固定根口径，评估报告 §8.3）。
    /// 安全：物理解析（含 junction/符号链接）后必须位于 jobs 根之下且相对路径恰为两级
    /// （<c>&lt;owner&gt;/s&lt;id&gt;</c>）——防误删 jobs 根或整个 owner 目录；递归删除跳过重解析点。
    /// </summary>
    public static (bool Removed, int Files, string? Error) TryRemoveSessionDirInJobsRoot(string jobsRoot, string sessionDir)
    {
        try
        {
            var root = PathGuard.TryCanonicalize(jobsRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = PathGuard.TryCanonicalize(sessionDir)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root is null || full is null)
                return (false, 0, "路径无法规范化");
            if (!PathGuard.IsUnder(root, full) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return (false, 0, "仅允许删除 jobs 根之下的会话目录");
            if (RelativeDepth(root, full) != 2)
                return (false, 0, "仅允许删除 <owner>/s<id> 两级会话目录");
            if (!Directory.Exists(full))
                return (false, 0, null);

            var files = Directory.EnumerateFiles(full, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Count();
            Directory.Delete(full, recursive: true);
            return (true, files, null);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    /// <summary>
    /// 清扫孤儿会话目录（评估报告 §8.2 附件保留策略）：jobs 根下 <c>&lt;owner&gt;/s&lt;id&gt;</c> 中，
    /// id 不在 <paramref name="aliveSessionIds"/>、且目录最后写入早于「现在 − retentionDays」的目录删除
    /// （防刚被删除/正在创建的目录误判）；清空后的 owner 目录顺带删除。
    /// <paramref name="retentionDays"/> ≤ 0 = 关闭清扫。跳过重解析点；失败项记入 <c>Errors</c>（上限 16 条）。
    /// </summary>
    public static (int RemovedDirs, int Files, string[] Errors) SweepOrphanSessionDirs(
        string jobsRoot, int retentionDays, IReadOnlyCollection<long> aliveSessionIds, DateTime? utcNow = null)
    {
        if (retentionDays <= 0)
            return (0, 0, Array.Empty<string>());

        var errors = new List<string>();
        var removedDirs = 0;
        var removedFiles = 0;
        try
        {
            var root = PathGuard.TryCanonicalize(jobsRoot);
            if (root is null || !Directory.Exists(root))
                return (0, 0, Array.Empty<string>());

            var cutoff = (utcNow ?? DateTime.UtcNow).ToUniversalTime().AddDays(-retentionDays);
            var alive = aliveSessionIds.ToHashSet();

            foreach (var ownerDir in Directory.EnumerateDirectories(root))
            {
                if (IsReparsePoint(ownerDir))
                    continue;

                foreach (var sessionDir in Directory.EnumerateDirectories(ownerDir))
                {
                    if (IsReparsePoint(sessionDir))
                        continue;
                    if (!TryParseSessionId(Path.GetFileName(sessionDir), out var id) || alive.Contains(id))
                        continue;
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(sessionDir) >= cutoff)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    var (removed, files, error) = TryRemoveSessionDirInJobsRoot(root, sessionDir);
                    if (removed)
                    {
                        removedDirs++;
                        removedFiles += files;
                    }
                    else if (error is not null && errors.Count < 16)
                    {
                        errors.Add($"{Path.GetFileName(ownerDir)}/{Path.GetFileName(sessionDir)}: {error}");
                    }
                }

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(ownerDir).Any())
                        Directory.Delete(ownerDir, recursive: false);
                }
                catch
                {
                    // 空目录清理失败不影响结果
                }
            }
        }
        catch (Exception ex)
        {
            if (errors.Count < 16)
                errors.Add(ex.Message);
        }

        return (removedDirs, removedFiles, errors.ToArray());
    }

    private static int RelativeDepth(string root, string full) =>
        Path.GetRelativePath(root, full)
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .Length;

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;   // 属性读取失败按不可信处理（跳过）
        }
    }

    /// <summary>
    /// 删除会话工作目录（评估报告 §5.3.5：附件/会话产出随会话删除清理，保留策略可配见 §8）。
    /// 安全：仅允许删除 <c>&lt;sharedRoot&gt;/jobs/</c> 子树内的目录——PathGuard 物理解析（含 junction/符号链接）
    /// 防止删掉共享根本身、jobs 根、jobs 外目录或经链接指向他处的路径；递归删除跳过重解析点不跟随链接。
    /// 返回：是否实际删除、删除的文件数（尽力统计）、失败原因（null=正常/无需处理）。
    /// </summary>
    public static (bool Removed, int Files, string? Error) TryRemoveSessionDir(string sharedRoot, string sessionDir)
    {
        try
        {
            var shared = PathGuard.TryCanonicalize(sharedRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = PathGuard.TryCanonicalize(sessionDir)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (shared is null || full is null)
                return (false, 0, "路径无法规范化");
            if (string.Equals(full, shared, StringComparison.OrdinalIgnoreCase))
                return (false, 0, "拒绝删除共享工作区根");
            var jobsRoot = Path.Combine(shared, JobsDirName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!PathGuard.IsUnder(jobsRoot, full) || string.Equals(full, jobsRoot, StringComparison.OrdinalIgnoreCase))
                return (false, 0, "仅允许删除共享区 jobs/ 内的会话目录");
            if (!Directory.Exists(full))
                return (false, 0, null);

            var files = Directory.EnumerateFiles(full, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Count();
            Directory.Delete(full, recursive: true);
            return (true, files, null);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

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
