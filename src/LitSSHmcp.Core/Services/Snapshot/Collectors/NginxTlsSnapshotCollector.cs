using System.Globalization;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Snapshot.Collectors;

/// <summary>
/// nginx 站点 TLS 快照：nginx -T 全量配置解析出 server_name/listen/ssl_certificate，
/// 再对证书批量 openssl 取到期日与 SAN（域名/IP 甄别）。
/// nginx 不存在时整体 skipped（多数机器本就没装 nginx，不算快照失败）。
/// </summary>
public sealed class NginxTlsSnapshotCollector : ISnapshotCollector
{
    public string Name => "nginx_tls";
    public int Order => 30;

    /// <summary>证书读取上限（防超长命令行）与"即将过期"提醒阈值（天）。</summary>
    private const int MaxCertificates = 15;
    public const int ExpiringSoonDays = 30;

    // 优先用"正在运行的 nginx 进程"的实际可执行路径执行 -T（如宝塔在 /www/server/nginx/sbin/nginx，
    // 与 PATH 里的系统 nginx 是两套配置），拿不到再回退 PATH。set -e 不启用，失败可继续。
    private const string ConfigCommand =
        "NGX=''; for p in $(pgrep -x nginx 2>/dev/null); do e=$(readlink /proc/$p/exe 2>/dev/null); " +
        "case \"$e\" in */nginx) NGX=$e; break;; esac; done; " +
        "[ -z \"$NGX\" ] && NGX=$(command -v nginx 2>/dev/null); " +
        "if [ -z \"$NGX\" ]; then echo '##nginx_missing'; else \"$NGX\" -T 2>&1 | head -2000; fi";

    private static readonly Regex ServerBlockPattern = new(@"^server\s*\{?\s*$", RegexOptions.Compiled);
    private static readonly Regex CommonNamePattern = new(@"CN\s*=\s*([^,/]+)", RegexOptions.Compiled);

    public sealed record NginxListen(string Raw, string? Bind, int? Port, bool Ssl);

    public sealed class NginxSite
    {
        public List<string> ServerNames { get; } = new();
        public List<NginxListen> Listens { get; } = new();
        public List<string> CertPaths { get; } = new();
        public bool HasTls => CertPaths.Count > 0 || Listens.Any(l => l.Ssl);
    }

    public async Task<CollectorResult> CollectAsync(SnapshotContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var configResult = await context.ExecuteAsync(ConfigCommand, context.Elevate, ct);
        if (!configResult.Success && configResult.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(configResult) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var configOutput = configResult.Output;
        // 整行精确匹配哨兵: 交互式 PTY 会回显命令行(其中含 '##nginx_missing' 字面量),
        // 用 Contains 会在 su 提权时把"其实已采集到配置"误判为"未安装"。
        if (configOutput.Replace("\r\n", "\n").Split('\n').Any(l => l.Trim() == "##nginx_missing"))
            return CollectorResult.Skip("未检测到 nginx(进程与 PATH 均无)") with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var (sites, truncated) = ParseNginxConfig(configOutput);
        if (sites.Count == 0)
        {
            // 空输出或 nginx 自身报错(nginx: [emerg] ...)都算失败——"没解析出站点"必须区分于"环境不具备"
            var errorLine = configOutput.Replace("\r\n", "\n").Split('\n')
                .FirstOrDefault(l => l.StartsWith("nginx: [", StringComparison.Ordinal) ||
                                     l.Contains(":[emerg]", StringComparison.Ordinal));
            if (errorLine is not null)
                return CollectorResult.Fail($"nginx -T 失败: {errorLine.Trim()}", configResult.ErrorKind)
                    with { DurationMs = sw.Elapsed.TotalMilliseconds };
            if (string.IsNullOrWhiteSpace(configOutput))
                return CollectorResult.Fail("nginx -T 输出为空(配置错误或权限不足), 无法提取证书", configResult.ErrorKind)
                    with { DurationMs = sw.Elapsed.TotalMilliseconds };
        }

        // phase2: 批量读证书（一次往返）；证书目录常为 root-only, 跟随整体提权决策
        var certPaths = sites.SelectMany(s => s.CertPaths).Distinct().Take(MaxCertificates).ToList();
        var certificates = new List<Dictionary<string, object?>>();
        var certErrors = 0;

        if (certPaths.Count > 0)
        {
            var command = string.Join("; ", certPaths.Select(path =>
                $"echo '##CERT:{path}'; openssl x509 -in {ShellQuote.Single(path)} -noout -enddate -subject -ext subjectAltName 2>&1 | head -30"));
            var certResult = await context.ExecuteAsync(command, context.Elevate, ct);
            if (certResult.ErrorKind is not null && !certResult.Success)
            {
                sw.Stop();
                return CollectorResult.FromCommandFailure(certResult) with { DurationMs = sw.Elapsed.TotalMilliseconds };
            }
            certificates = ParseCertificateBlocks(certResult.Output, DateTime.UtcNow, out certErrors);
        }

        var degraded = certErrors > 0;
        var note = certErrors > 0
            ? $"{certErrors} 个证书读取失败(权限/路径/openssl缺失), 如需完整视图请启用 snapshot.useSudo"
            : null;

        var data = new Dictionary<string, object?>
        {
            ["available"] = true,
            ["serverCount"] = sites.Count,
            ["sites"] = sites.Select(ToSiteDto).ToList(),
            // 域名列表: 所有 server_name; 启用 SSL 的域名挂上对应证书信息(便于"哪些域名有证书/何时过期")
            ["domains"] = BuildDomains(sites, certificates),
            ["certificates"] = certificates,
            ["expiringSoonDays"] = ExpiringSoonDays,
            ["expiringSoon"] = certificates
                .Where(c => c.TryGetValue("daysRemaining", out var d) && d is long days && days <= ExpiringSoonDays)
                .Select(c => c["path"])
                .ToList(),
            ["certificateCount"] = certificates.Count,
            // nginx -T 校验后的完整有效配置(含 include 展开)
            ["effectiveConfig"] = configOutput,
            ["configTruncated"] = truncated
        };

        sw.Stop();
        return CollectorResult.Ok(data, degraded, note) with { DurationMs = sw.Elapsed.TotalMilliseconds };
    }

    /// <summary>
    /// 汇总"域名 → 是否启用 SSL / 监听端口 / 证书信息"。同一域名出现在多个 server 块时合并
    /// （ssl 取或、端口并集、证书按路径去重）；非 TLS 域名也会列出但 certificates 为空。
    /// </summary>
    public static List<Dictionary<string, object?>> BuildDomains(
        List<NginxSite> sites, List<Dictionary<string, object?>> certificates)
    {
        var certByPath = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var cert in certificates)
        {
            if (cert.TryGetValue("path", out var p) && p is string path && path.Length > 0)
                certByPath[path] = cert;
        }

        var domains = new Dictionary<string, DomainAgg>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            var certs = site.CertPaths.Where(certByPath.ContainsKey).Select(p => certByPath[p]).ToList();
            var ssl = certs.Count > 0 || site.Listens.Any(l => l.Ssl);
            var ports = site.Listens.Where(l => l.Port.HasValue).Select(l => l.Port!.Value).ToList();

            foreach (var name in site.ServerNames)
            {
                if (string.IsNullOrWhiteSpace(name) || name == "_")
                    continue;

                if (!domains.TryGetValue(name, out var agg))
                    domains[name] = agg = new DomainAgg(name);

                agg.Ssl |= ssl;
                agg.Ports.UnionWith(ports);
                foreach (var cert in certs)
                {
                    var certPath = cert.GetValueOrDefault("path") as string;
                    if (agg.Certs.All(c => !ReferenceEquals(c, cert) && (c.GetValueOrDefault("path") as string) != certPath))
                        agg.Certs.Add(cert);
                }
            }
        }

        return domains.Values
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new Dictionary<string, object?>
            {
                ["domain"] = d.Name,
                ["ssl"] = d.Ssl,
                ["ports"] = d.Ports.OrderBy(p => p).ToArray(),
                ["certificates"] = d.Certs.Select(c => new Dictionary<string, object?>
                {
                    ["path"] = c.GetValueOrDefault("path"),
                    ["subjectCn"] = c.GetValueOrDefault("subjectCn"),
                    ["notAfter"] = c.GetValueOrDefault("notAfter"),
                    ["daysRemaining"] = c.GetValueOrDefault("daysRemaining"),
                    ["expired"] = c.GetValueOrDefault("expired"),
                    ["dnsNames"] = c.GetValueOrDefault("dnsNames")
                }).ToList()
            })
            .ToList();
    }

    private sealed class DomainAgg
    {
        public DomainAgg(string name) => Name = name;
        public string Name { get; }
        public bool Ssl { get; set; }
        public HashSet<int> Ports { get; } = new();
        public List<Dictionary<string, object?>> Certs { get; } = new();
    }

    private static Dictionary<string, object?> ToSiteDto(NginxSite site) => new()
    {
        ["serverNames"] = site.ServerNames,
        ["listens"] = site.Listens.Select(l => new Dictionary<string, object?>
        {
            ["raw"] = l.Raw,
            ["bind"] = l.Bind,
            ["port"] = l.Port,
            ["ssl"] = l.Ssl
        }).ToList(),
        ["certificatePaths"] = site.CertPaths
    };

    /// <summary>
    /// 解析 nginx -T 全量输出为 server 块（含 include 展开后的内容）。
    /// 括号深度跟踪定位 server{} 边界；截断（head -2000）返回 truncated=true。
    /// </summary>
    public static (List<NginxSite> Sites, bool Truncated) ParseNginxConfig(string output)
    {
        var sites = new List<NginxSite>();
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var truncated = lines.Length >= 2000;

        NginxSite? current = null;
        var serverDepth = 0;
        var entered = false;   // 是否已见到 server 块的 '{'（宝塔等配置把 '{' 写在下一行）
        var outerDepth = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var opens = line.Count(c => c == '{');
            var closes = line.Count(c => c == '}');

            if (current is null)
            {
                if (ServerBlockPattern.IsMatch(line))   // "server" 或 "server {"（'{' 允许在下一行）
                {
                    current = new NginxSite();
                    serverDepth = 0;
                    entered = false;
                    // 继续处理本行花括号
                }
                else
                {
                    outerDepth += opens - closes;
                    continue;
                }
            }

            // server 块内部(已进入花括号): 提取字段
            if (entered)
            {
                if (line.StartsWith("server_name ", StringComparison.Ordinal))
                {
                    var names = line["server_name".Length..].Trim().TrimEnd(';')
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    current.ServerNames.AddRange(names.Where(n => n != "_"));
                }
                else if (line.StartsWith("listen ", StringComparison.Ordinal))
                {
                    var listen = ParseListen(line["listen".Length..].Trim().TrimEnd(';'));
                    if (listen is not null)
                        current.Listens.Add(listen);
                }
                else if (line.StartsWith("ssl_certificate ", StringComparison.Ordinal))
                {
                    var path = line["ssl_certificate".Length..].Trim().TrimEnd(';').Trim();
                    if (path.Length > 0 && !path.Contains('$'))   // 含变量的证书路径无法静态读取
                        current.CertPaths.Add(path);
                }
            }

            serverDepth += opens - closes;
            if (serverDepth >= 1)
                entered = true;

            if (entered && serverDepth <= 0)
            {
                if (current.HasTls || current.Listens.Count > 0 || current.ServerNames.Count > 0)
                    sites.Add(current);
                current = null;
                serverDepth = 0;
                entered = false;
            }
        }

        return (sites.Take(50).ToList(), truncated || sites.Count > 50);
    }

    /// <summary>"443 ssl" / "[::]:443 ssl http2" / "80" → listen 记录；无法解析端口时返回 null。</summary>
    public static NginxListen? ParseListen(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return null;

        var endpoint = tokens[0];
        if (endpoint.StartsWith("unix:", StringComparison.Ordinal))
            return new NginxListen(value, endpoint, null, tokens.Contains("ssl"));

        string? bind = null;
        var portPart = endpoint;
        var colon = endpoint.LastIndexOf(':');
        if (colon >= 0)
        {
            bind = endpoint[..colon].Trim('[', ']');
            portPart = endpoint[(colon + 1)..];
        }
        if (!int.TryParse(portPart, out var port))
            return null;

        return new NginxListen(value, bind, port, tokens.Contains("ssl"));
    }

    /// <summary>
    /// 解析逐证书 openssl 输出（##CERT:path 分段）为证书记录；
    /// 含 notAfter/subject/SAN，读取失败的证书记录 error 并计入 errorCount。
    /// </summary>
    public static List<Dictionary<string, object?>> ParseCertificateBlocks(string output, DateTime nowUtc, out int errorCount)
    {
        var result = new List<Dictionary<string, object?>>();
        var blocks = new List<(string Path, List<string> Lines)>();
        (string Path, List<string> Lines)? current = null;

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("##CERT:", StringComparison.Ordinal))
            {
                var path = line["##CERT:".Length..].Trim();
                // 只接受"干净路径"的标记行: su PTY 回显/换行碎片会以 ##CERT: 开头但带引号等噪音, 需忽略
                if (IsCleanCertPath(path))
                {
                    if (current is not null)
                        blocks.Add(current.Value);
                    current = (path, new List<string>());
                }
            }
            else if (current is not null)
            {
                current.Value.Lines.Add(line);
            }
        }
        if (current is not null)
            blocks.Add(current.Value);

        errorCount = 0;
        foreach (var (path, lines) in blocks)
        {
            string? notAfterRaw = null;
            string? subjectLine = null;
            var dnsNames = new List<string>();
            var ipNames = new List<string>();
            string? error = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("notAfter=", StringComparison.Ordinal))
                    notAfterRaw = line["notAfter=".Length..].Trim();
                else if (line.StartsWith("subject=", StringComparison.Ordinal))
                    subjectLine = line["subject=".Length..].Trim();
                else if (line.Contains("DNS:", StringComparison.Ordinal) || line.Contains("IP Address:", StringComparison.Ordinal))
                    ParseSanLine(line, dnsNames, ipNames);
                else if (line.Length > 0 && error is null &&
                         (line.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains("Could not read", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains("unable to load", StringComparison.OrdinalIgnoreCase)))
                    error = line;
            }

            DateTime? notAfter = null;
            if (notAfterRaw is not null)
                notAfter = ParseOpenSslDate(notAfterRaw);

            if (notAfter is null && error is null)
                error = "证书输出无法解析(notAfter 缺失)";
            if (error is not null)
                errorCount++;

            var cn = subjectLine is null ? null : CommonNamePattern.Match(subjectLine) is { Success: true } m
                ? m.Groups[1].Value.Trim()
                : subjectLine;

            result.Add(new Dictionary<string, object?>
            {
                ["path"] = path,
                ["subjectCn"] = cn,
                ["notAfter"] = notAfter?.ToString("O"),
                ["daysRemaining"] = notAfter is null ? (long?)null : (long)Math.Floor((notAfter.Value - nowUtc).TotalDays),
                ["expired"] = notAfter is not null && notAfter < nowUtc,
                ["dnsNames"] = dnsNames.Distinct().ToList(),
                ["ipNames"] = ipNames.Distinct().ToList(),
                ["error"] = error
            });
        }

        return result;
    }

    private static void ParseSanLine(string line, List<string> dnsNames, List<string> ipNames)
    {
        foreach (var part in line.Split(','))
        {
            var entry = part.Trim();
            if (entry.StartsWith("DNS:", StringComparison.Ordinal))
                dnsNames.Add(entry["DNS:".Length..].Trim());
            else if (entry.StartsWith("IP Address:", StringComparison.Ordinal))
                ipNames.Add(entry["IP Address:".Length..].Trim());
        }
    }

    /// <summary>证书路径标记必须"干净"：不含引号/分号/命令替换等 PTY 回显噪音字符。</summary>
    private static bool IsCleanCertPath(string path) =>
        path.Length > 0 && path.IndexOfAny(new[] { '\'', '"', ';', '$', '`', '(', ')', '{', '}' }) < 0;

    /// <summary>
    /// 解析 OpenSSL 日期 "Jan 15 12:00:00 2027 GMT"（%b %e，个位数日期会多一个空格）。
    /// InvariantCulture 的 DateTime.Parse 不认英文月份+日的组合，故归一化空白后用精确格式解析。
    /// </summary>
    private static DateTime? ParseOpenSslDate(string raw)
    {
        var text = raw.Replace(" GMT", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return DateTime.TryParseExact(text, "MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }
}
