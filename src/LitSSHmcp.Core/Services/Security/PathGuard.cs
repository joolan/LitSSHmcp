namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 路径物理规范化的安全防护（评估报告 §10.C.3）。
/// <see cref="Path.GetFullPath(string)"/> 只做字面归一化，不会解析 junction / 符号链接：
/// 允许根之内放一个指向根外的链接，纯前缀检查会被绕过（"链接逃逸"）。
/// 本类逐段解析路径中已存在的 reparse point 得到物理路径，再做前缀归属判断。
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// 把路径解析为物理规范路径：逐段拼接，凡遇到目录/文件链接（junction/symlink/reparse point）
    /// 即解析为其最终目标并从目标继续；不存在的尾段按字面保留（允许创建新文件）。
    /// 解析失败返回 null（调用方应按"拒绝"处理）。
    /// </summary>
    public static string? TryCanonicalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return null;

            var rest = full[root.Length..];
            if (rest.Length == 0)
                return root;

            var segments = rest.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                var resolved = ResolveSegment(current);
                if (resolved is not null)
                    current = resolved;
            }

            return current;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 若该段是链接（目录 junction / 符号链接 / 文件符号链接），返回解析后的最终目标路径；否则 null。
    /// </summary>
    private static string? ResolveSegment(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = dir.ResolveLinkTarget(returnFinalTarget: true);
                    return target?.FullName;
                }
                return null;
            }

            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = file.ResolveLinkTarget(returnFinalTarget: true);
                    return target?.FullName;
                }
            }

            return null;
        }
        catch
        {
            // 属性/链接读取失败：视为不可信，不解析（保持字面路径，交由前缀判断）
            return null;
        }
    }

    /// <summary>
    /// 归属判断（安全边界用）：路径规范化并解析链接后，必须位于允许根之下（含根自身）。
    /// 根与路径都做物理解析——允许根本身是指向他处的链接时，以其真实目标为边界。
    /// </summary>
    public static bool IsUnder(string root, string? path)
    {
        var rootFull = TryCanonicalize(root);
        var pathFull = TryCanonicalize(path);
        if (rootFull is null || pathFull is null)
            return false;

        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        return pathFull.Equals(rootPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase)
               || pathFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
