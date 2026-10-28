using System.Text.RegularExpressions;

namespace LitSSHmcp.Agent;

/// <summary>
/// 轻量查询改写：把中英文运维同义词扩展成 FTS 词项，提升跨语言/近义词召回。
/// 不调用模型，规则固定、可单测；结果仍交给 FTS5/BM25 + 向量混合排序。
/// </summary>
public static class MemoryQueryRewriter
{
    private const int MaxTerms = 24;

    private static readonly string[][] Synonyms =
    [
        ["磁盘", "disk", "df", "filesystem"],
        ["内存", "memory", "mem", "oom"],
        ["负载", "load", "cpu", "processor"],
        ["日志", "log", "logs", "journal"],
        ["证书", "cert", "certificate", "tls", "ssl"],
        ["端口", "port", "netstat", "ss"],
        ["重启", "restart", "reboot"],
        ["发布", "deploy", "release", "rollout"],
        ["回滚", "rollback", "revert"],
        ["数据库", "database", "mysql", "postgres", "redis"],
        ["容器", "docker", "container"],
        ["服务", "service", "systemd"],
        ["网络", "network", "ping", "curl", "dns"],
        ["超时", "timeout", "timed"],
        ["磁盘满", "diskfull", "df"],
    ];

    public static IReadOnlyList<string> ExpandTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();
        var terms = Regex.Matches(query, @"[\p{L}\p{N}_-]+")
            .Select(m => m.Value.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var expanded = new List<string>(terms);
        foreach (var term in terms)
        {
            foreach (var group in Synonyms)
            {
                if (!group.Any(s => Matches(term, s)))
                    continue;
                foreach (var synonym in group)
                {
                    if (!expanded.Contains(synonym, StringComparer.OrdinalIgnoreCase))
                        expanded.Add(synonym);
                    if (expanded.Count >= MaxTerms)
                        return expanded;
                }
            }
        }
        return expanded.Take(MaxTerms).ToList();
    }

    private static bool Matches(string term, string synonym) =>
        string.Equals(term, synonym, StringComparison.OrdinalIgnoreCase) ||
        (term.Length >= 2 && synonym.Length >= 2 &&
         (term.Contains(synonym, StringComparison.OrdinalIgnoreCase) ||
          synonym.Contains(term, StringComparison.OrdinalIgnoreCase)));
}
