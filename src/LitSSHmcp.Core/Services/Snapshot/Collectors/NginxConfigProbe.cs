using System.Text.RegularExpressions;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.Core.Services.Snapshot.Collectors;

/// <summary>nginx/tengine/openresty 检测结果。</summary>
public sealed record NginxDetectResult(string Bin, string Version, string ServerType);

/// <summary>
/// 一个 nginx 实例。Pid=0 表示通过 PATH/常见目录/用户目录发现的未运行安装。
/// ConfPath/Prefix 来自运行进程的 -c/-p 参数，优先用于 nginx -T（否则会读到编译默认配置）。
/// </summary>
public sealed record NginxInstance(
    int Pid,
    int? Uid,
    string Kind,
    string Bin,
    string? Cwd,
    string? ConfPath,
    string? Prefix,
    string Source)
{
    public bool HasBinary => !string.IsNullOrWhiteSpace(Bin);
    public bool IsMaster => string.Equals(Kind, "master", StringComparison.OrdinalIgnoreCase);
    public bool IsWorker => string.Equals(Kind, "worker", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// nginx 配置探测的共享实现：二进制检测、运行实例 -c/-p、-T 命令、-T 失败/截断时的配置文件兜底。
/// 快照采集器与 HTTP 站点探测共用，避免两套命令与 Tengine 识别逻辑分叉。
/// </summary>
public static class NginxConfigProbe
{
    public const int MaxConfigLines = 20000;

    /// <summary>
    /// 进程优先发现 nginx/tengine/openresty：读取 /proc/&lt;pid&gt;/exe、cwd、Uid 与 cmdline 的 -c/-p；
    /// 没有运行实例时再查 PATH、用户目录、常见安装目录，最后在 $HOME 做有限深度查找。
    /// 输出格式：##NGX_PROC/##NGX_CAND 制表符分隔，另输出 ##NGINX_BIN/##BIN 供旧调用方解析。
    /// </summary>
    public const string DiscoverCommand =
        "NGX_FOUND=''; " +
        "for d in /proc/[0-9]*; do " +
        "p=${d#/proc/}; " +
        "[ -r \"$d/comm\" ] || continue; " +
        "comm=$(cat \"$d/comm\" 2>/dev/null) || continue; " +
        "case \"$comm\" in nginx|tengine|openresty) ;; *) continue;; esac; " +
        "exe=$(readlink \"$d/exe\" 2>/dev/null); " +
        "[ -z \"$exe\" ] && exe=$(command -v \"$comm\" 2>/dev/null); " +
        "cwd=$(readlink \"$d/cwd\" 2>/dev/null); " +
        "uid=$(awk '/^Uid:/{print $2; exit}' \"$d/status\" 2>/dev/null); " +
        "args=$(tr '\\0' '\\n' < \"$d/cmdline\" 2>/dev/null); " +
        "conf=$(printf '%s\\n' \"$args\" | awk 'prev==\"-c\"{print;exit} /^-c[^-]/{sub(/^-c/,\"\");print;exit} {prev=$0}'); " +
        "prefix=$(printf '%s\\n' \"$args\" | awk 'prev==\"-p\"{print;exit} /^-p[^-]/{sub(/^-p/,\"\");print;exit} {prev=$0}'); " +
        "[ -z \"$conf\" ] && conf=$(printf '%s\\n' \"$args\" | sed -n 's/.* -c *\\([^ ]*\\).*/\\1/p' | head -1); " +
        "[ -z \"$prefix\" ] && prefix=$(printf '%s\\n' \"$args\" | sed -n 's/.* -p *\\([^ ]*\\).*/\\1/p' | head -1); " +
        "kind=process; case \"$args\" in *worker*) kind=worker;; *master*) kind=master;; esac; " +
        "printf '##NGX_PROC\\t%s\\t%s\\t%s\\t%s\\t%s\\t%s\\t%s\\n' \"$p\" \"${uid:-?}\" \"$kind\" \"$exe\" \"${cwd:-}\" \"${conf:-}\" \"${prefix:-}\"; " +
        "[ -z \"$exe\" ] || [ -n \"$NGX_FOUND\" ] || NGX_FOUND=$exe; " +
        "done; " +
        "emit(){ [ -x \"$1\" ] || return; root=${1%/sbin/*}; conf=''; prefix=''; " +
        "if [ \"$root\" != \"$1\" ] && [ -f \"$root/conf/nginx.conf\" ]; then conf=$root/conf/nginx.conf; prefix=$root; fi; " +
        "printf '##NGX_CAND\\t0\\t\\tpath\\t%s\\t\\t%s\\t%s\\n' \"$1\" \"$conf\" \"$prefix\"; " +
        "[ -n \"$NGX_FOUND\" ] || NGX_FOUND=$1; }; " +
        "for p in \"$HOME\"/nginx/sbin/nginx \"$HOME\"/openresty/nginx/sbin/nginx \"$HOME\"/openresty/bin/openresty " +
        "\"$HOME\"/tengine/sbin/nginx \"$HOME\"/apps/nginx/sbin/nginx \"$HOME\"/opt/nginx/sbin/nginx \"$HOME\"/.local/nginx/sbin/nginx " +
        "/root/nginx/sbin/nginx /root/openresty/nginx/sbin/nginx " +
        "/usr/sbin/nginx /usr/local/nginx/sbin/nginx /www/server/nginx/sbin/nginx " +
        "/usr/local/openresty/nginx/sbin/nginx /usr/local/openresty/bin/openresty " +
        "/usr/local/tengine/sbin/nginx /www/server/tengine/sbin/nginx; do emit \"$p\"; done; " +
        "for p in \"$HOME\"/*/sbin/nginx \"$HOME\"/*/*/sbin/nginx \"$HOME\"/opt/*/sbin/nginx " +
        "/home/*/nginx/sbin/nginx /home/*/openresty/nginx/sbin/nginx /home/*/*/sbin/nginx /home/*/*/*/sbin/nginx; do [ -e \"$p\" ] && emit \"$p\"; done; " +
        "if [ -z \"$NGX_FOUND\" ]; then for p in $(find \"$HOME\" -maxdepth 4 -type f \\( -name nginx -o -name openresty \\) -perm -u+x 2>/dev/null | head -5); do emit \"$p\"; done; fi; " +
        "if [ -n \"$NGX_FOUND\" ]; then echo \"##NGINX_BIN $NGX_FOUND\"; echo \"##BIN $NGX_FOUND\"; \"$NGX_FOUND\" -v 2>&1; fi; true";

    /// <summary>兼容旧调用方：检测运行中的 nginx/tengine；输出 ##BIN 与 -v 版本行。</summary>
    public const string DetectCommand = DiscoverCommand;

    private static readonly Regex BinPattern = new(@"^##BIN\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(
        @"(?:nginx|Tengine|openresty)\s+version:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>解析 DiscoverCommand 输出；未发现返回空列表。</summary>
    public static List<NginxInstance> ParseInstances(string? output)
    {
        var instances = new List<NginxInstance>();
        if (string.IsNullOrWhiteSpace(output))
            return instances;

        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("##NGX_PROC\t", StringComparison.Ordinal) ||
                line.StartsWith("##NGX_CAND\t", StringComparison.Ordinal))
            {
                var fields = line.Split('\t');
                if (fields.Length < 8)
                    continue;
                _ = int.TryParse(fields[1], out var pid);
                int? uid = int.TryParse(fields[2], out var parsedUid) ? parsedUid : null;
                instances.Add(new NginxInstance(
                    pid,
                    uid,
                    fields[3],
                    fields[4],
                    EmptyToNull(fields[5]),
                    EmptyToNull(fields[6]),
                    EmptyToNull(fields[7]),
                    fields[0] == "##NGX_PROC" ? "process" : "path"));
                continue;
            }

            if (line.StartsWith("##NGINX_BIN ", StringComparison.Ordinal) ||
                line.StartsWith("##BIN ", StringComparison.Ordinal))
            {
                var bin = line[(line.IndexOf(' ') + 1)..].Trim();
                if (bin.Length > 0 && !instances.Any(instance =>
                        string.Equals(instance.Bin, bin, StringComparison.Ordinal)))
                {
                    instances.Add(new NginxInstance(0, null, "path", bin, null, null, null, "path"));
                }
            }
        }

        // 同一二进制/配置的去重：进程优先于 PATH，master 优先于 worker。
        var map = new Dictionary<string, NginxInstance>(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            var key = instance.HasBinary
                ? $"{instance.Bin}\u0001{instance.ConfPath}\u0001{instance.Prefix}"
                : $"pid:{instance.Pid}";
            if (!map.TryGetValue(key, out var existing) || Rank(instance) < Rank(existing))
                map[key] = instance;
        }

        return map.Values
            .OrderBy(Rank)
            .ThenBy(instance => instance.Bin, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>选择主实例：优先可读的 master/process，其次 PATH/用户目录发现。</summary>
    public static NginxInstance? PrimaryInstance(IReadOnlyList<NginxInstance> instances)
        => instances
            .Where(instance => instance.HasBinary)
            .OrderBy(Rank)
            .FirstOrDefault()
           ?? instances.FirstOrDefault(instance => !instance.IsWorker)
           ?? instances.FirstOrDefault();

    /// <summary>解析 DetectCommand 输出；未检测到返回 null。</summary>
    public static NginxDetectResult? ParseDetect(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var instance = PrimaryInstance(ParseInstances(output));
        var match = BinPattern.Match(output);
        var bin = instance?.Bin;
        if (string.IsNullOrWhiteSpace(bin) && match.Success)
            bin = match.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(bin))
            return null;

        var versionLine = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains("version:", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var serverType = versionLine.Contains("tengine", StringComparison.OrdinalIgnoreCase) ||
                         bin.Contains("tengine", StringComparison.OrdinalIgnoreCase)
            ? "Tengine"
            : versionLine.Contains("openresty", StringComparison.OrdinalIgnoreCase) ||
              bin.Contains("openresty", StringComparison.OrdinalIgnoreCase)
                ? "OpenResty"
                : "Nginx";
        var version = VersionPattern.Match(versionLine).Groups[1].Value.Trim();
        if (version.Length == 0)
            version = versionLine;
        return new NginxDetectResult(bin, version, serverType);
    }

    /// <summary>生成按运行实例 -p/-c 执行的 `nginx -T` 命令（带输出行数上限）。</summary>
    public static string BuildConfigCommand(string bin, int maxLines = MaxConfigLines) =>
        BuildConfigCommand(new NginxInstance(0, null, "path", bin, null, null, null, "path"), maxLines);

    /// <summary>生成按运行实例 -p/-c 执行的 `nginx -T` 命令（带输出行数上限）。</summary>
    public static string BuildConfigCommand(NginxInstance instance, int maxLines = MaxConfigLines)
    {
        var args = new List<string> { ShellQuote.Single(instance.Bin) };
        if (!string.IsNullOrWhiteSpace(instance.Prefix))
        {
            args.Add("-p");
            args.Add(ShellQuote.Single(instance.Prefix));
        }
        if (!string.IsNullOrWhiteSpace(instance.ConfPath))
        {
            args.Add("-c");
            args.Add(ShellQuote.Single(ResolveConfPath(instance)));
        }
        args.Add("-T");
        return string.Join(" ", args) + $" 2>&1 | head -{Math.Max(100, maxLines)}";
    }

    /// <summary>nginx -T 不可用时的兜底：按 --conf-path/--prefix/用户目录/常见安装目录读取主配置 + vhost/conf.d。</summary>
    public static string BuildFallbackConfigCommand(string bin)
        => BuildFallbackConfigCommand(new NginxInstance(0, null, "path", bin, null, null, null, "path"));

    /// <summary>nginx -T 不可用时的兜底：优先使用运行实例的 -c/-p，再按 --conf-path/--prefix/常见目录读取。</summary>
    public static string BuildFallbackConfigCommand(NginxInstance instance)
    {
        var q = ShellQuote.Single(instance.Bin);
        var conf = ResolveConfPath(instance);
        var confExpr = string.IsNullOrWhiteSpace(conf)
            ? "$(" + q + " -V 2>&1 | sed -n 's/.*--conf-path=\\([^ ]*\\).*/\\1/p' | head -1)"
            : ShellQuote.Single(conf);
        var prefixExpr = string.IsNullOrWhiteSpace(instance.Prefix)
            ? "$(" + q + " -V 2>&1 | sed -n 's/.*--prefix=\\([^ ]*\\).*/\\1/p' | head -1)"
            : ShellQuote.Single(instance.Prefix);

        return
            "CONF=" + confExpr + "; PREFIX=" + prefixExpr + "; " +
            "[ -z \"$CONF\" ] && [ -n \"$PREFIX\" ] && [ -f \"$PREFIX/conf/nginx.conf\" ] && CONF=\"$PREFIX/conf/nginx.conf\"; " +
            "[ -z \"$CONF\" ] && for f in /etc/nginx/nginx.conf /usr/local/nginx/conf/nginx.conf /usr/local/tengine/conf/nginx.conf " +
            "/www/server/nginx/conf/nginx.conf /www/server/tengine/conf/nginx.conf /usr/local/openresty/nginx/conf/nginx.conf " +
            "\"$HOME/nginx/conf/nginx.conf\" \"$HOME/openresty/nginx/conf/nginx.conf\" \"$HOME/tengine/conf/nginx.conf\" " +
            "\"$HOME/apps/nginx/conf/nginx.conf\" \"$HOME/opt/nginx/conf/nginx.conf\"; do [ -f \"$f\" ] && CONF=$f && break; done; " +
            "if [ -n \"$CONF\" ] && [ -f \"$CONF\" ]; then ROOT=$(dirname \"$CONF\"); " +
            "for f in \"$CONF\" $(find \"$ROOT\" -maxdepth 4 -type f \\( -name '*.conf' -o -path '*/sites-enabled/*' -o -path '*/vhost/*' \\) 2>/dev/null) " +
            "/www/server/panel/vhost/nginx/*.conf /www/server/nginx/conf/vhost/*.conf /usr/local/tengine/conf/vhost/*.conf; do " +
            "[ -f \"$f\" ] || continue; echo \"# configuration file $f\"; cat \"$f\"; done; fi";
    }

    private static string ResolveConfPath(NginxInstance instance)
    {
        var conf = instance.ConfPath;
        if (string.IsNullOrWhiteSpace(conf) || conf.StartsWith("/", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(instance.Prefix))
            return conf ?? string.Empty;
        return instance.Prefix.TrimEnd('/') + "/" + conf.TrimStart('/');
    }

    private static int Rank(NginxInstance instance) => instance.Kind.ToLowerInvariant() switch
    {
        "master" => 0,
        "process" => 1,
        "worker" => 2,
        _ => 3
    };

    private static string? EmptyToNull(string value)
        => string.IsNullOrWhiteSpace(value) || value == "?" ? null : value;
}
