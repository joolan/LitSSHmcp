using System.Text;

namespace LitSSHmcp.Agent;

/// <summary>
/// 大工具结果的落盘存储：把超长结果写入文件，上下文只保留预览 + <c>spill://&lt;handle&gt;</c> 句柄，
/// 模型再用 <c>spill_read</c>/<c>spill_grep</c> 按需分段读取，避免一次性把大输出灌进上下文。
/// </summary>
public sealed class SpillStore
{
    public const string Scheme = "spill://";

    private readonly string _dir;

    public SpillStore(string dir, int retentionDays)
    {
        _dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(_dir);
        if (retentionDays > 0)
            Prune(retentionDays);
    }

    public string Dir => _dir;

    /// <summary>保存完整结果，返回句柄（文件名）。</summary>
    public string Save(string toolName, string content)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(toolName)}-{Guid.NewGuid():N}.txt";
        File.WriteAllText(Path.Combine(_dir, id), content, Encoding.UTF8);
        return id;
    }

    /// <summary>把句柄解析为落盘文件路径（限制在目录内，防越界）。</summary>
    public string? Resolve(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
            return null;
        var id = handle.Trim();
        if (id.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            id = id[Scheme.Length..];
        if (id.Contains("..", StringComparison.Ordinal) || id.IndexOfAny(new[] { '/', '\\' }) >= 0)
            return null;

        var full = Path.GetFullPath(Path.Combine(_dir, id));
        var prefix = _dir.EndsWith(Path.DirectorySeparatorChar) ? _dir : _dir + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public IReadOnlyList<string> List() =>
        Directory.Exists(_dir)
            ? Directory.EnumerateFiles(_dir, "*.txt").Select(Path.GetFileName).Where(n => n is not null).Cast<string>()
                .OrderByDescending(n => n, StringComparer.Ordinal).Take(200).ToList()
            : Array.Empty<string>();

    private void Prune(int retentionDays)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            foreach (var file in Directory.EnumerateFiles(_dir, "*.txt"))
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
        }
        catch
        {
            // 清理失败忽略
        }
    }

    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "tool";
        var chars = name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray();
        return chars.Length == 0 ? "tool" : new string(chars);
    }
}
