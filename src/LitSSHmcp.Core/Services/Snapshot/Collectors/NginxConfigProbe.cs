using System.Text.RegularExpressions;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.Core.Services.Snapshot.Collectors;

/// <summary>nginx/tengine/openresty 检测结果。</summary>
public sealed record NginxDetectResult(string Bin, string Version, string ServerType);

/// <summary>
/// nginx 配置探测的共享实现：二进制检测、-T 命令、-T 失败/截断时的配置文件兜底。
/// 快照采集器与 HTTP 站点探测共用，避免两套命令与 Tengine 识别逻辑分叉。
/// </summary>
public static class NginxConfigProbe
{
    public const int MaxConfigLines = 20000;

    /// <summary>检测运行中的 nginx/tengine；输出 ##BIN 与 -v 版本行。</summary>
    public const string DetectCommand =
        "NGX=''; for p in $(pgrep -x nginx 2>/dev/null) $(pgrep -x tengine 2>/dev/null); do " +
        "e=$(readlink /proc/$p/exe 2>/dev/null); case \"$e\" in */nginx|*/tengine) NGX=$e; break;; esac; done; " +
        "[ -z \"$NGX\" ] && NGX=$(command -v nginx 2>/dev/null || command -v tengine 2>/dev/null); " +
        "if [ -n \"$NGX\" ]; then echo \"##BIN $NGX\"; \"$NGX\" -v 2>&1; fi";

    private static readonly Regex BinPattern = new(@"^##BIN\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(
        @"(?:nginx|Tengine|openresty)\s+version:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>解析 DetectCommand 输出；未检测到返回 null。</summary>
    public static NginxDetectResult? ParseDetect(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        var match = BinPattern.Match(output);
        if (!match.Success)
            return null;

        var bin = match.Groups[1].Value.Trim();
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

    /// <summary>生成 `nginx -T` 命令（带输出行数上限）。</summary>
    public static string BuildConfigCommand(string bin, int maxLines = MaxConfigLines) =>
        $"{ShellQuote.Single(bin)} -T 2>&1 | head -{Math.Max(100, maxLines)}";

    /// <summary>
    /// nginx -T 不可用时的兜底：按 --conf-path 或常见安装目录读取主配置 + vhost/conf.d。
    /// </summary>
    public static string BuildFallbackConfigCommand(string bin)
    {
        var q = ShellQuote.Single(bin);
        return
            "CONF=$(" + q + " -V 2>&1 | sed -n 's/.*--conf-path=\\([^ ]*\\).*/\\1/p' | head -1); " +
            "[ -z \"$CONF\" ] && for f in /etc/nginx/nginx.conf /usr/local/nginx/conf/nginx.conf /usr/local/tengine/conf/nginx.conf /www/server/nginx/conf/nginx.conf /www/server/tengine/conf/nginx.conf /usr/local/openresty/nginx/conf/nginx.conf; do [ -f \"$f\" ] && CONF=$f && break; done; " +
            "if [ -n \"$CONF\" ] && [ -f \"$CONF\" ]; then ROOT=$(dirname \"$CONF\"); " +
            "for f in \"$CONF\" $(find \"$ROOT\" -maxdepth 4 -type f \\( -name '*.conf' -o -path '*/sites-enabled/*' -o -path '*/vhost/*' \\) 2>/dev/null) /www/server/panel/vhost/nginx/*.conf /www/server/nginx/conf/vhost/*.conf /usr/local/tengine/conf/vhost/*.conf; do " +
            "[ -f \"$f\" ] || continue; echo \"# configuration file $f\"; cat \"$f\"; done; fi";
    }
}
