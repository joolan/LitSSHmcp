namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 远端文件名/相对路径的本地落盘防护：只接受可安全映射到 Windows 的单段名称，
/// 拒绝分隔符、盘符/ADS、父目录、控制字符、保留设备名与尾随点/空格。
/// </summary>
public static class RemotePathGuard
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".msix", ".appx",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".scr", ".pif", ".lnk", ".reg", ".cpl", ".dll", ".jar",
        ".py", ".sh", ".bash", ".zsh", ".fish", ".command", ".app", ".desktop"
    };

    public static bool TryFileName(string? name, out string safe)
    {
        safe = string.Empty;
        if (!TrySegment(name, out var segment))
            return false;
        safe = segment;
        return true;
    }

    public static bool TryRelativePath(string? relative, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(relative))
            return false;
        if (relative[0] is '/' or '\\')
            return false;

        var parts = relative.Split('/');
        var segments = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TrySegment(parts[i], out segments[i]))
                return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>把远端相对路径映射到本地根内；不安全或逃逸（含链接）时返回 null。</summary>
    public static string? ResolveLocalUnder(string root, string relative)
    {
        if (!TryRelativePath(relative, out var normalized))
            return null;

        try
        {
            var rootFull = Path.GetFullPath(root);
            var local = Path.GetFullPath(Path.Combine(rootFull,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            return PathGuard.IsUnder(rootFull, local) ? local : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsPotentiallyExecutableFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return ExecutableExtensions.Contains(Path.GetExtension(name));
    }

    private static bool TrySegment(string? segment, out string safe)
    {
        safe = string.Empty;
        if (string.IsNullOrEmpty(segment) || segment is "." or "..")
            return false;
        if (segment.Length > 255)
            return false;
        if (segment.EndsWith('.') || segment.EndsWith(' '))
            return false;

        foreach (var c in segment)
        {
            if (char.IsControl(c) || c is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*')
                return false;
        }

        var stem = segment.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem))
            return false;

        safe = segment;
        return true;
    }
}
