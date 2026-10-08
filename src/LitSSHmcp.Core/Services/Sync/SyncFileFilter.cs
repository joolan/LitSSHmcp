using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Sync;

/// <summary>同步文件过滤：按 include/exclude（glob，匹配相对路径或文件名）筛选待同步文件。</summary>
public static class SyncFileFilter
{
    public static Dictionary<string, (long Size, DateTime Mtime)> Apply(
        IReadOnlyDictionary<string, (long Size, DateTime Mtime)> files, SyncTaskConfig task)
    {
        var include = task.IncludePatterns ?? Array.Empty<string>();
        var exclude = task.ExcludePatterns ?? Array.Empty<string>();
        var result = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
        foreach (var (rel, meta) in files)
        {
            if (IsIncluded(rel, include, exclude))
                result[rel] = meta;
        }
        return result;
    }

    public static bool IsIncluded(string relativePath, IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        var name = relativePath.Contains('/')
            ? relativePath[(relativePath.LastIndexOf('/') + 1)..]
            : relativePath;

        if (exclude.Count > 0 && exclude.Any(p => GlobMatch(p, relativePath) || GlobMatch(p, name)))
            return false;
        if (include.Count > 0 && !include.Any(p => GlobMatch(p, relativePath) || GlobMatch(p, name)))
            return false;
        return true;
    }

    /// <summary>简单 glob：支持 * 与 ?（大小写不敏感）。</summary>
    public static bool GlobMatch(string pattern, string text)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;
        var regex = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase);
    }
}
