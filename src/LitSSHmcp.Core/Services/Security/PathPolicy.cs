namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 文件传输路径白名单校验（A4）。用于约束上传/下载的本地与远程路径。
/// </summary>
public static class PathPolicy
{
    /// <summary>
    /// 本地路径是否落在允许的根目录内（Windows 大小写不敏感）。
    /// M1（评估报告 §10.C.3）：经 <see cref="PathGuard"/> 解析 junction/符号链接后判断归属，
    /// 防止允许根内的链接指向根外造成"链接逃逸"。
    /// </summary>
    public static bool IsLocalPathAllowed(string path, string[]? allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(path) || allowedRoots == null || allowedRoots.Length == 0)
            return false;

        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            if (PathGuard.IsUnder(root, path))
                return true;
        }

        return false;
    }

    /// <summary>远程路径是否落在允许的根目录内（POSIX，拒绝 .. 穿越）。</summary>
    public static bool IsRemotePathAllowed(string path, string[]? allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(path) || allowedRoots == null || allowedRoots.Length == 0)
            return false;

        var normalized = NormalizeRemote(path);
        if (normalized == null)
            return false;

        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var rootNorm = NormalizeRemote(root);
            if (rootNorm == null)
                continue;

            if (normalized == rootNorm || normalized.StartsWith(rootNorm + "/", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>规范化远程路径；包含 .. 穿越或无法规范化时返回 null。</summary>
    private static string? NormalizeRemote(string path)
    {
        var p = path.Replace('\\', '/').Trim();
        if (p.Length == 0)
            return null;

        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (stack.Count == 0)
                    return null; // 试图越过根
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(part);
        }

        return "/" + string.Join('/', stack);
    }
}
