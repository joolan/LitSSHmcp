using System.Text.RegularExpressions;

namespace LitSSHmcp.Core.Models;

public class CommandFilterConfig
{
    // —— 内置默认规则 ——
    // 放在类内而不是 CreateDefaultConfig, 是为了让"配置里缺少 commandFilter 段"这种情形
    // 也仍然有最基本的防护(否则反序列化会保留空数组 = 全部放行, 即 fail-open)。
    // 用户若显式写了 "blockedCommands": [] 等, 反序列化会覆盖成空数组, 表示有意关闭, 这里不干预。

    public static readonly string[] DefaultBlockedCommands =
    {
        "rm -rf /",
        "mkfs",
        "dd if=/dev/zero",
        ":(){ :|:& };:",
        "chmod -R 777 /",
        "wget | sh",
        "curl | sh",
        // Docker 高危：清库/清卷/清网络/删服务/退出集群/特权或挂根目录运行
        "docker system prune",
        "docker volume prune",
        "docker network prune",
        "docker volume rm",
        "docker service rm",
        "docker swarm leave",
        "docker run --privileged",
        "docker run -v /"
    };

    public static readonly string[] DefaultSensitiveCommands =
    {
        "rm ",
        "chmod",
        "chown",
        "systemctl stop",
        "systemctl restart",
        "reboot",
        "shutdown",
        "kill",
        "pkill",
        "apt remove",
        "yum remove",
        // Docker 写/运维操作（需桌面确认；只读的 docker ps/logs/inspect/stats 不受限）
        "docker rm",
        "docker rmi",
        "docker kill",
        "docker stop",
        "docker restart",
        "docker run",
        "docker exec",
        "docker cp",
        "docker compose down",
        "docker compose rm"
    };

    public static readonly string[] DefaultSensitivePatterns =
    {
        "\\brm\\b",
        "\\bchmod\\b",
        "\\bchown\\b",
        "\\breboot\\b",
        "\\bshutdown\\b",
        "\\bkill\\b"
    };

    public string[] BlockedCommands { get; set; } = DefaultBlockedCommands;
    public string[] SensitiveCommands { get; set; } = DefaultSensitiveCommands;
    public string[] SensitivePatterns { get; set; } = DefaultSensitivePatterns;

    private Regex[]? _compiledBlocked;
    private Regex[]? _compiledSensitiveCommands;
    private Regex[]? _compiledSensitivePatterns;

    public bool IsBlocked(string command) =>
        MatchesAny(command, ref _compiledBlocked, BlockedCommands);

    public bool IsSensitive(string command) =>
        MatchesAny(command, ref _compiledSensitiveCommands, SensitiveCommands)
        || MatchesAnyPattern(command, ref _compiledSensitivePatterns, SensitivePatterns);

    /// <summary>
    /// 命令条目的匹配采用"整词/整段"匹配, 而不是裸的 Contains —— 否则 "chmod" 会命中 "xchmodz",
    /// "rm" 会命中 "format" 之类, 造成误报; 而前缀/后缀又必须允许命令行的其它部分。
    /// 条目内部的空白按一个或多个空白处理, 兼容多余空格与 tab。
    /// </summary>
    private static bool MatchesAny(string command, ref Regex[]? cache, string[] entries)
    {
        if (entries.Length == 0)
            return false;

        cache ??= entries
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(CompileEntry)
            .ToArray();

        return cache.Any(r => r.IsMatch(command));
    }

    private static bool MatchesAnyPattern(string command, ref Regex[]? cache, string[] patterns)
    {
        if (patterns.Length == 0)
            return false;

        cache ??= patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();

        return cache.Any(r => r.IsMatch(command));
    }

    private static Regex CompileEntry(string entry)
    {
        var trimmed = entry.Trim();
        var body = string.Join(@"\s+", trimmed
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Regex.Escape));

        // 只在条目两端本身是"词字符"(字母/数字/下划线)时才要求词边界；
        // 例如 "rm -rf /" 以 '/' 结尾，后面紧跟 "var"(rm -rf /var) 也必须算命中。
        var prefix = IsWordChar(trimmed[0]) ? @"(?<![A-Za-z0-9_])" : string.Empty;
        var suffix = IsWordChar(trimmed[^1]) ? @"(?![A-Za-z0-9_])" : string.Empty;

        return new Regex(prefix + body + suffix, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
