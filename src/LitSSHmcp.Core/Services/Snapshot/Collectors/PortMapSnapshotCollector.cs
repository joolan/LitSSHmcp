using System.Text.RegularExpressions;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Snapshot.Collectors;

/// <summary>
/// 端口↔进程名/PID/用户↔systemd服务 三元组（root 视图）。
/// phase1: ss 取 TCP/UDP 监听与 Unix socket（提权时能看到 root 进程的 users(...) 与属主）；
/// phase2: 按 pid 批量取 ps 属主与 cgroup 服务名（单次往返）。
/// 区分 TCP/UDP、双栈(0.0.0.0 / :: / *)与 Unix socket；未提权时降级并标注。
/// </summary>
public sealed class PortMapSnapshotCollector : ISnapshotCollector
{
    public string Name => "portmap";
    public int Order => 20;

    // 不依赖 "##标记" 分段: su 交互式 PTY 会回显命令, 换行后可能恰好把标记单独成行而污染解析。
    // 改为按"行特征"在 C# 侧区分: ss 监听行(状态列)/Unix 行(u_*)/ps 行(数字开头)/cgroup 行(/cgroup:)。
    private const string SsCommand =
        "ss -H -tlnp 2>/dev/null; ss -H -lunp 2>/dev/null; ss -H -xlp 2>/dev/null | head -120";

    private static readonly Regex UsersPattern = new(@"users:\(\(""([^""]+)"",pid=(\d+)", RegexOptions.Compiled);

    public async Task<CollectorResult> CollectAsync(SnapshotContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var degraded = !context.Elevate;
        var degradeNote = degraded ? "未启用提权: 非本用户的进程属主/服务归属可能缺失" : null;

        var ssResult = await context.ExecuteAsync(SsCommand, context.Elevate, ct);
        if (!ssResult.Success && ssResult.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(ssResult) with { DurationMs = sw.Elapsed.TotalMilliseconds };
        if (!ssResult.Success && string.IsNullOrWhiteSpace(ssResult.Output))
            return CollectorResult.FromCommandFailure(ssResult) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        // 一次输出里按行特征分类（proto 传 null 表示由状态推断）：
        var listeners = ParseSsListeners(ssResult.Output, proto: null);
        var unixAll = ParseSsUnix(ssResult.Output);

        // phase2: 批量取属主与 cgroup 服务名（一次往返；pid 上限防超长命令行）。
        // cgroup 只取 systemd 控制器行（v2: 0::/...；v1: N:name=systemd:/...），避免 v1 每 pid 多行被 head 截断。
        var pids = listeners.Select(l => l.TryGetValue("pid", out var p) ? p as long? : null)
            .Concat(unixAll.Select(u => u.TryGetValue("pid", out var p) ? p as long? : null))
            .Where(p => p is > 0)
            .Select(p => p!.Value)
            .Distinct()
            .OrderBy(p => p)
            .Take(200)
            .ToList();

        if (pids.Count > 0)
        {
            var pidCsv = string.Join(',', pids);
            // 注意: 用分开的 -o（而非 pid=,user=,comm=）—— CentOS7 的 procps-ng 3.3.10 对
            // "首个逗号列带 = 抑制表头"的写法解析有缺陷(只输出 pid 一列), 分开写则正常。
            // 末段用 readlink 取程序路径、/proc/<pid>/cmdline 取完整启动命令行（NUL→空格，不截断）；
            // 用 "EXE <pid> <path>" / "CMD <pid> <cmdline>" 前缀便于无歧义解析。
            var exeLoop = "for p in " + pidCsv.Replace(',', ' ') +
                          "; do printf 'EXE %s %s\\n' \"$p\" \"$(readlink /proc/$p/exe 2>/dev/null)\"; " +
                          "printf 'CMD %s %s\\n' \"$p\" \"$(tr '\\0' ' ' < /proc/$p/cmdline 2>/dev/null)\"; done";
            var infoCommand =
                $"ps -o pid= -o user:24= -o comm= -p {pidCsv} 2>/dev/null; " +
                $"grep -HE '0::|name=systemd' {string.Join(' ', pids.Select(pid => $"/proc/{pid}/cgroup"))} 2>/dev/null | head -400; " +
                exeLoop;
            var infoResult = await context.ExecuteAsync(infoCommand, context.Elevate, ct);
            if (infoResult.Success || !string.IsNullOrWhiteSpace(infoResult.Output))
            {
                // 无标记: 同一份输出里 ps 行以数字开头, cgroup 行含 /cgroup:, EXE 行以 "EXE " 开头, 各自跳过对方的行
                var users = ParsePs(infoResult.Output);
                var cgroups = ParseCgroup(infoResult.Output);
                var exePaths = ParseExePaths(infoResult.Output);
                var cmdlines = ParseCmdlines(infoResult.Output);
                foreach (var row in listeners.Concat(unixAll))
                {
                    if (row.TryGetValue("pid", out var pidObj) && pidObj is long pid)
                    {
                        if (users.TryGetValue(pid, out var ps) && row["user"] is null)
                        {
                            row["user"] = ps.User;
                            if (row["process"] is null)
                                row["process"] = ps.Comm;
                        }
                        if (cgroups.TryGetValue(pid, out var cg) && row["unit"] is null)
                        {
                            row["unit"] = cg.Unit;
                            row["cgroup"] = cg.Path;
                        }
                        if (exePaths.TryGetValue(pid, out var exe) && row["exe"] is null)
                            row["exe"] = exe;
                        if (cmdlines.TryGetValue(pid, out var cmdline) && row["cmdline"] is null)
                            row["cmdline"] = MaskSecretArgs(cmdline);
                    }
                }
            }
        }

        // 桌面/用户会话 socket(gnome/pipewire/dbus/X11/ibus 等)默认折叠: 主列表只给系统级 socket + 网络监听,
        // 桌面态放 unixSocketsDesktop 供需要时查看。
        var unixSystem = unixAll.Where(r => ClassifyUnixSocket(r) == "system").ToList();
        var unixDesktop = unixAll.Where(r => ClassifyUnixSocket(r) == "desktop").ToList();

        var truncated = listeners.Count > 200 || unixAll.Count > 200;
        listeners = listeners.Take(200).ToList();
        unixSystem = unixSystem.Take(200).ToList();
        unixDesktop = unixDesktop.Take(200).ToList();

        var data = new Dictionary<string, object?>
        {
            ["counts"] = new Dictionary<string, object?>
            {
                ["tcp"] = listeners.Count(l => Equals(l["proto"], "tcp")),
                ["udp"] = listeners.Count(l => Equals(l["proto"], "udp")),
                ["unix"] = unixSystem.Count,
                ["unixDesktop"] = unixDesktop.Count
            },
            ["listeners"] = listeners,
            ["unixSockets"] = unixSystem,
            ["unixSocketsDesktop"] = unixDesktop,
            ["elevated"] = context.Elevate,
            ["truncated"] = truncated
        };

        sw.Stop();
        return CollectorResult.Ok(data, degraded, degradeNote) with { DurationMs = sw.Elapsed.TotalMilliseconds };
    }

    private static readonly string[] DesktopProcessMarkers =
    {
        "gnome", "gdm", "ibus", "gvfs", "pipewire", "pulseaudio", "wireplumber",
        "tracker", "evolution", "at-spi", "colord", "dconf", "xdg-desktop", "gsd-", "gjs"
    };

    /// <summary>
    /// 判定 Unix socket 是"桌面/用户会话"还是"系统级"。系统级才进主列表。
    /// 判据：路径在 /run/user、含 /user-、抽象(@开头)或 /tmp/ 下；或进程属于常见桌面组件。
    /// </summary>
    public static string ClassifyUnixSocket(Dictionary<string, object?> socket)
    {
        var path = socket.GetValueOrDefault("path") as string ?? string.Empty;
        var process = (socket.GetValueOrDefault("process") as string ?? string.Empty).ToLowerInvariant();

        var desktop = path.StartsWith("/run/user/", StringComparison.Ordinal) ||
                      path.Contains("/user-", StringComparison.Ordinal) ||
                      path.StartsWith("@", StringComparison.Ordinal) ||
                      path.Contains("/tmp/", StringComparison.Ordinal) ||
                      process is "x" or "xorg" ||
                      DesktopProcessMarkers.Any(process.Contains);
        return desktop ? "desktop" : "system";
    }

    /// <summary>
    /// 解析 ss 监听行:
    /// <c>LISTEN 0 511 0.0.0.0:80 0.0.0.0:* users:(("nginx",pid=123,fd=6))</c>。
    /// <paramref name="proto"/> 为空时按状态推断（LISTEN→tcp, UNCONN/BOUND→udp），并跳过 Unix 行（u_*）与 PTY 回显的
    /// 命令行（首列既非状态也非 u_*）；显式传 "tcp"/"udp" 时按对应状态过滤。
    /// </summary>
    public static List<Dictionary<string, object?>> ParseSsListeners(string output, string? proto)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 5)
                continue;

            var state = tokens[0];
            var resolved = proto;
            if (string.IsNullOrEmpty(resolved))
            {
                if (state.Equals("LISTEN", StringComparison.OrdinalIgnoreCase))
                    resolved = "tcp";
                else if (state.Equals("UNCONN", StringComparison.OrdinalIgnoreCase) ||
                         state.Equals("BOUND", StringComparison.OrdinalIgnoreCase))
                    resolved = "udp";
                else
                    continue;   // u_* / ss 命令回显 / 其它状态
            }
            else if (resolved == "tcp")
            {
                if (!state.Equals("LISTEN", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else if (resolved == "udp")
            {
                if (!state.Equals("UNCONN", StringComparison.OrdinalIgnoreCase) &&
                    !state.Equals("LISTEN", StringComparison.OrdinalIgnoreCase) &&
                    !state.Equals("BOUND", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (!TrySplitAddress(tokens[3], out var bind, out var port))
                continue;

            var row = new Dictionary<string, object?>
            {
                ["proto"] = resolved,
                ["state"] = state,
                ["bind"] = bind,
                ["port"] = port,
                ["family"] = FamilyOf(bind),
                ["anyBind"] = IsAnyBind(bind)
            };
            ApplyProcess(line, row);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>解析 ss -xlp 行: <c>u_str LISTEN 0 128 /run/systemd/private 5520 [users:(...)]</c>。</summary>
    public static List<Dictionary<string, object?>> ParseSsUnix(string output)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 5 || !tokens[0].StartsWith("u_", StringComparison.Ordinal))
                continue;

            var row = new Dictionary<string, object?>
            {
                ["type"] = tokens[0] switch
                {
                    "u_str" => "stream",
                    "u_dgr" => "datagram",
                    "u_seq" => "seqpacket",
                    "u_raw" => "raw",
                    "u_rdm" => "rdm",
                    _ => tokens[0]
                },
                ["state"] = tokens[1],
                ["path"] = tokens[4]
            };
            ApplyProcess(line, row);
            rows.Add(row);
        }
        return rows;
    }

    public sealed record PsInfo(string User, string Comm);
    public sealed record CgroupInfo(string Path, string? Unit);

    /// <summary>解析 ps -o pid=,user=,comm= 输出 → pid → (user, comm)。</summary>
    public static Dictionary<long, PsInfo> ParsePs(string output)
    {
        var result = new Dictionary<long, PsInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3 && long.TryParse(tokens[0], out var pid))
                result[pid] = new PsInfo(tokens[1], string.Join(' ', tokens[2..]));
        }
        return result;
    }

    /// <summary>
    /// 解析 grep -H 输出 <c>/proc/123/cgroup:0::/system.slice/nginx.service</c> → pid → (cgroup, unit)。
    /// v1 格式 <c>1:name=systemd:/system.slice/x.service</c> 同样适用（取最后一个冒号后的路径）。
    /// </summary>
    public static Dictionary<long, CgroupInfo> ParseCgroup(string output)
    {
        var result = new Dictionary<long, CgroupInfo>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();   // 去掉行尾 \r（Windows 换行/PTY 输出）
            var marker = line.IndexOf("/cgroup:", StringComparison.Ordinal);
            if (marker <= 6 || marker + 8 >= line.Length)
                continue;
            if (!long.TryParse(line[6..marker], out var pid))
                continue;

            var content = line[(marker + 8)..];
            var colon = content.LastIndexOf(':');
            var path = colon >= 0 ? content[(colon + 1)..] : content;

            var unit = (path.LastIndexOf('/') is var lastSlash && lastSlash >= 0 ? path[(lastSlash + 1)..] : path)
                .TrimEnd('/');
            if (!unit.EndsWith(".service", StringComparison.Ordinal))
                unit = null;

            result[pid] = new CgroupInfo(path, unit);
        }
        return result;
    }

    /// <summary>"0.0.0.0:80" / "[::]:443" / "*:80" → (地址, 端口)。返回 false 表示行格式异常。</summary>
    public static bool TrySplitAddress(string local, out string bind, out int port)
    {
        bind = local;
        port = 0;
        var colon = local.LastIndexOf(':');
        if (colon <= 0 || colon == local.Length - 1)
            return false;
        if (!int.TryParse(local[(colon + 1)..], out port))
            return false;
        bind = local[..colon].Trim('[', ']');
        return true;
    }

    /// <summary>0.0.0.0→ipv4, ::→ipv6, *→any(双栈通配), 具体地址按是否含冒号判族。</summary>
    public static string FamilyOf(string bind) => bind switch
    {
        "*" => "any",
        "0.0.0.0" => "ipv4",
        "::" => "ipv6",
        _ when bind.Contains(':') => "ipv6",
        _ => "ipv4"
    };

    public static bool IsAnyBind(string bind) => bind is "*" or "0.0.0.0" or "::";

    /// <summary>解析 "EXE &lt;pid&gt; &lt;path&gt;" 行（readlink /proc/pid/exe 的结果）→ pid → 程序路径。
    /// 解析失败/权限不足/进程已退出时该行可缺失或为空，均安全忽略。</summary>
    public static Dictionary<long, string> ParseExePaths(string output) => ParsePidValueLines(output, "EXE ");

    /// <summary>解析 "CMD &lt;pid&gt; &lt;cmdline&gt;" 行（/proc/pid/cmdline 的结果）→ pid → 完整启动命令行。</summary>
    public static Dictionary<long, string> ParseCmdlines(string output) => ParsePidValueLines(output, "CMD ");

    private static Dictionary<long, string> ParsePidValueLines(string output, string prefix)
    {
        var result = new Dictionary<long, string>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var rest = line[prefix.Length..];
            var space = rest.IndexOf(' ');
            if (space <= 0 || !long.TryParse(rest[..space], out var pid))
                continue;

            var value = rest[(space + 1)..].Trim();
            if (value.Length > 0)
                result[pid] = value;
        }
        return result;
    }

    /// <summary>常见凭据参数脱敏（password/passwd/pwd/secret/token/api_key/access_key 的值 → ******），
    /// 避免进程启动命令里的密码被写进快照。</summary>
    private static readonly Regex SecretArgPattern = new(
        "(?i)\\b(password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key)(\\s*=\\s*|\\s+)([^\\s;\"']+)",
        RegexOptions.Compiled);

    public static string MaskSecretArgs(string cmdline) =>
        SecretArgPattern.Replace(cmdline, "$1$2******");

    private static void ApplyProcess(string line, Dictionary<string, object?> row)
    {
        var match = UsersPattern.Match(line);
        if (match.Success)
        {
            row["process"] = match.Groups[1].Value;
            row["pid"] = long.TryParse(match.Groups[2].Value, out var pid) ? pid : (long?)null;
        }
        else
        {
            row["process"] = null;
            row["pid"] = null;
        }
        row["user"] = null;
        row["unit"] = null;
        row["exe"] = null;
        row["cmdline"] = null;
    }
}
