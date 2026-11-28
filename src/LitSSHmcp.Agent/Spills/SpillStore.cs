using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LitSSHmcp.Agent;

/// <summary>
/// 大工具结果的落盘存储：把超长结果写入文件，上下文只保留预览 + <c>spill://&lt;handle&gt;</c> 句柄，
/// 模型再用 <c>spill(action=read|grep)</c> 按需分段读取/搜索，避免一次性把大输出灌进上下文。
/// </summary>
public sealed class SpillStore
{
    public const string Scheme = "spill://";

    private readonly string _dir;
    private readonly int _retentionDays;
    private int _savesSincePrune;
    private DateTime _lastPruneUtc;

    public SpillStore(string dir, int retentionDays, string? ownerUserId = null)
    {
        var root = Path.GetFullPath(dir);
        if (!string.IsNullOrWhiteSpace(ownerUserId))
            root = Path.Combine(root, WorkspaceLayout.SanitizeSegment(ownerUserId, "local"));
        _dir = root;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_dir);
        if (retentionDays > 0)
        {
            Prune(retentionDays);
            _lastPruneUtc = DateTime.UtcNow;
        }
    }

    public string Dir => _dir;

    /// <summary>保存完整结果，返回句柄（文件名）。每 50 次保存或距上次清理超 1 小时增量 Prune，避免长会话磁盘无界增长。</summary>
    public string Save(string toolName, string content)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(toolName)}-{Guid.NewGuid():N}.txt";
        File.WriteAllText(Path.Combine(_dir, id), FormatForStorage(content), Encoding.UTF8);
        if (_retentionDays > 0)
        {
            var count = Interlocked.Increment(ref _savesSincePrune);
            var due = DateTime.UtcNow - _lastPruneUtc > TimeSpan.FromHours(1);
            if (count >= 50 || due)
            {
                Interlocked.Exchange(ref _savesSincePrune, 0);
                _lastPruneUtc = DateTime.UtcNow;
                Prune(_retentionDays);
            }
        }
        return id;
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    /// <summary>单行 JSON 落盘前格式化，避免 spill read 只能读到超长单行。</summary>
    private static string FormatForStorage(string content)
    {
        var trimmed = content.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return content;
        try
        {
            var node = JsonNode.Parse(content);
            return node is null ? content : node.ToJsonString(PrettyJson);
        }
        catch (JsonException)
        {
            return content;
        }
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
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    /// <summary>最近 200 个句柄（按文件名倒序）。更早句柄虽在磁盘上但不列入，可凭摘要中的 spill:// 直接 read。</summary>
    public IReadOnlyList<string> List() =>
        Directory.Exists(_dir)
            ? Directory.EnumerateFiles(_dir, "*.txt").Select(Path.GetFileName).Where(n => n is not null).Cast<string>()
                .OrderByDescending(n => n, StringComparer.Ordinal).Take(200).ToList()
            : Array.Empty<string>();

    /// <summary>落盘文件总数（用于提示 List() 是否截断）。</summary>
    public int CountAll() =>
        Directory.Exists(_dir) ? Directory.EnumerateFiles(_dir, "*.txt").Count() : 0;

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
