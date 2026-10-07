using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.SSH;

/// <summary>跨机复制（源→目标）的认证方式。</summary>
public enum RemoteCopyAuthMode
{
    /// <summary>优先使用源服务器已有的免密（key/agent），否则回退到 sshpass+目标密码。</summary>
    Auto,
    /// <summary>仅使用源服务器已有的免密；不可用则报错。</summary>
    KeyOnly,
    /// <summary>使用目标服务器密码（需源服务器安装 sshpass）。</summary>
    Password
}

/// <summary>跨机复制的数据通道。</summary>
public enum RemoteCopyTransport
{
    /// <summary>自动：优先 rsync，退化 tar|ssh。</summary>
    Auto,
    Rsync,
    Tar,
    Relay
}

/// <summary>跨机复制请求：让「源服务器」直接把所选路径推送到「目标服务器」（数据走服务器间通道）。</summary>
public sealed class RemoteCopyRequest
{
    public required SshServerConfig Source { get; init; }

    /// <summary>源服务器上的绝对路径列表（文件或目录）。</summary>
    public required IReadOnlyList<string> SourcePaths { get; init; }

    public required SshServerConfig Target { get; init; }

    /// <summary>目标地址（已由界面按「主机地址 / 内网地址」解析）。</summary>
    public required string TargetHost { get; init; }

    public required int TargetPort { get; init; }

    /// <summary>目标目录（须存在或可创建）。</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>rsync/tar 传输启用压缩（-z）。</summary>
    public bool Compress { get; init; } = true;

    /// <summary>目标已存在同名项时是否覆盖。</summary>
    public bool Overwrite { get; init; } = true;

    public RemoteCopyAuthMode AuthMode { get; init; } = RemoteCopyAuthMode.Auto;

    public RemoteCopyTransport Transport { get; init; } = RemoteCopyTransport.Auto;

    /// <summary>直连不可用时，是否允许回退为「经本机内存中转」（SFTP 读→写，不落盘）；默认允许。</summary>
    public bool AllowRelayFallback { get; init; } = true;

    public int TimeoutSeconds { get; init; } = 3600;
}

/// <summary>跨机复制结果。</summary>
public sealed class RemoteCopyResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>失败分类：bad_request / auth / dependency / host_key / network / timeout / transfer / unknown。</summary>
    public string ErrorKind { get; set; } = "unknown";

    /// <summary>实际使用的策略：rsync / tar / relay。</summary>
    public string Strategy { get; set; } = string.Empty;

    /// <summary>非致命提示（如"源机无 rsync，已改用 tar"）。</summary>
    public string? Warning { get; set; }

    /// <summary>
    /// 直连不可用、但请求允许内存中转时为 true：调用方应弹窗询问用户是否改用「经本机内存中转」，
    /// 确认后以 <see cref="RemoteCopyTransport.Relay"/> 再次调用。Core 不会自动中转。
    /// </summary>
    public bool NeedsRelayConfirmation { get; set; }

    /// <summary>失败原因是「源服务器缺少 sshpass」（导致无法用目标密码直连）：调用方可提示用户在源机安装后重试。</summary>
    public bool NeedsSshpassInstall { get; set; }

    public int ItemsTotal { get; set; }
    public int ItemsSucceeded { get; set; }
    public int ItemsFailed { get; set; }

    /// <summary>因「不覆盖同名」而跳过的同名文件数（仅在不覆盖模式下统计）。</summary>
    public int ItemsSkipped { get; set; }
    public long TotalBytes { get; set; }
    public long BytesTransferred { get; set; }
    public TimeSpan Duration { get; set; }

    /// <summary>源机传输命令的原始输出（进度/错误），便于排障（密码不会出现在其中）。</summary>
    public string Log { get; set; } = string.Empty;
}

/// <summary>
/// 跨机复制命令构建与进度解析（纯函数，便于单测）：
/// 让源机在自身 shell 中直接 rsync/tar|ssh 到目标，数据不经本机。
/// </summary>
public static class RemoteCopyCommandBuilder
{
    private static readonly Regex RsyncPercentRegex = new(@"(\d{1,3})%", RegexOptions.Compiled);
    private static readonly Regex PvPercentRegex = new(@"^\s*(\d{1,3})\s*$", RegexOptions.Compiled);

    public static bool HasInternal(SshServerConfig server) =>
        !string.IsNullOrWhiteSpace(server.InternalHost);

    /// <summary>解析目标端点：useInternal 且配置了内网地址时返回内网地址/端口，否则返回主机地址/端口。</summary>
    public static (string Host, int Port) ResolveEndpoint(SshServerConfig server, bool useInternal)
    {
        if (useInternal && HasInternal(server))
            return (server.InternalHost!, server.InternalPort is > 0 ? server.InternalPort.Value : server.Port);
        return (server.Host, server.Port);
    }

    /// <summary>POSIX 单引号安全转义。</summary>
    public static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    public static string ParentOf(string path)
    {
        var t = path.TrimEnd('/');
        var i = t.LastIndexOf('/');
        if (i < 0) return ".";
        return i == 0 ? "/" : t[..i];
    }

    public static string BaseNameOf(string path)
    {
        var t = path.TrimEnd('/');
        var i = t.LastIndexOf('/');
        return i < 0 ? t : t[(i + 1)..];
    }

    public static string BuildSshOptions(bool keyAuth, int port)
    {
        // 注意：不能用 accept-new（需 OpenSSH>=7.6，CentOS7 等旧客户端不支持）；用 no 兼容老版本。
        var opts = "-o StrictHostKeyChecking=no -o ConnectTimeout=10 -p " + port;
        opts += keyAuth
            ? " -o BatchMode=yes"
            : " -o PubkeyAuthentication=no -o PreferredAuthentications=password -o NumberOfPasswordPrompts=1";
        return opts;
    }

    /// <summary>源机能力/免密探测：一次命令返回 rsync/tar/sshpass/pv 是否存在，以及免密是否可用。</summary>
    public static string BuildProbeCommand(string targetHost, int targetPort, string targetUser)
    {
        var dest = ShellQuote(targetUser + "@" + targetHost);
        return string.Join("; ",
            "command -v rsync >/dev/null 2>&1 && echo LITSSH_HAS_RSYNC",
            "command -v tar >/dev/null 2>&1 && echo LITSSH_HAS_TAR",
            "command -v sshpass >/dev/null 2>&1 && echo LITSSH_HAS_SSHPASS",
            "command -v pv >/dev/null 2>&1 && echo LITSSH_HAS_PV",
            "ssh -o BatchMode=yes -o StrictHostKeyChecking=no -o ConnectTimeout=8 -p " + targetPort +
            " " + dest + " true >/dev/null 2>&1 && echo LITSSH_KEY_OK || echo LITSSH_KEY_FAIL");
    }

    /// <summary>目标端能力探测（rsync/tar 需两端都存在；tar 是否支持 --skip-old-files）：从本机连接目标执行。</summary>
    public static string BuildTargetProbeCommand() =>
        "command -v rsync >/dev/null 2>&1 && echo LITSSH_T_RSYNC; " +
        "command -v tar >/dev/null 2>&1 && echo LITSSH_T_TAR; " +
        "tar --help 2>&1 | grep -q -- 'skip-old-files' && echo LITSSH_T_TAR_SKIP";

    /// <summary>
    /// 在源机侧生成「将写入目标的文件绝对路径」列表（仅普通文件，用于在目标端统计同名已存在文件数）。
    /// 目录会递归展开，映射规则与 rsync/tar 一致（顶层名 + 相对路径）。
    /// </summary>
    public static string BuildDestFileListCommand(IReadOnlyList<string> sourcePaths, string targetDirectory)
    {
        var paths = string.Join(' ', sourcePaths.Select(ShellQuote));
        var baseQ = ShellQuote(targetDirectory);
        return "BASE=" + baseQ + "; for p in " + paths + "; do n=${p##*/}; " +
               "if [ -d \"$p\" ]; then ( cd \"$p\" && find . -type f -mindepth 1 2>/dev/null | sed 's|^\\./||' | while IFS= read -r r; do printf '%s/%s/%s\\n' \"$BASE\" \"$n\" \"$r\"; done ); " +
               "elif [ -f \"$p\" ]; then printf '%s/%s\\n' \"$BASE\" \"$n\"; " +
               "fi; done";
    }

    /// <summary>目标端统计 stdin 文件列表中「已存在的普通文件」数（输出 LITSSH_SKIPPED:N）。</summary>
    public static string BuildCountExistingCommand() =>
        "c=0; while IFS= read -r f; do [ -f \"$f\" ] && c=$((c+1)); done; echo LITSSH_SKIPPED:$c";

    /// <summary>源机侧统计所选路径总字节（用于进度）。</summary>
    public static string BuildMeasureSizeCommand(IReadOnlyList<string> paths)
    {
        var args = string.Join(' ', paths.Select(ShellQuote));
        return "du -sb -- " + args + " 2>/dev/null | awk '{s+=$1} END {printf \"%d\", s+0}'";
    }

    /// <summary>
    /// 构建源机执行的实际传输命令：
    /// - 优先 rsync（-a -s --partial --info=progress2 --no-inc-recursive [-z]）；
    /// - 无 rsync 时 tar|ssh（可选 pv 提供进度）；
    /// - usePassword 时用 sshpass+临时密码文件（密码经 stdin 写入，不进 argv）。
    /// </summary>
    public static string BuildDirectCommand(RemoteCopyRequest req, bool usePassword, bool useRsync, bool hasPv, long totalBytes, bool tarSkipOldFiles = false)
    {
        var dest = req.Target.Username + "@" + req.TargetHost;
        var sshOpts = BuildSshOptions(!usePassword, req.TargetPort);
        var sshCmd = "ssh " + sshOpts;
        var destQ = ShellQuote(dest);
        var auth = usePassword ? "sshpass -f \"$f\" " : "";
        var compress = req.Compress ? " -z" : "";

        string payload;
        if (useRsync)
        {
            var srcs = string.Join(' ', req.SourcePaths.Select(ShellQuote));
            var remoteSpec = ShellQuote(dest + ":" + req.TargetDirectory);
            var overwriteFlag = req.Overwrite ? string.Empty : " --ignore-existing";
            var rsync = "rsync -a -s --partial --info=progress2 --no-inc-recursive" + compress + overwriteFlag +
                        " -e \"" + sshCmd + "\" -- " + srcs + " " + remoteSpec;
            var mk = sshCmd + " " + destQ + " " + ShellQuote("mkdir -p -- " + ShellQuote(req.TargetDirectory));
            payload = auth + mk + " && " + auth + rsync;
        }
        else
        {
            var parts = new List<string>();
            var pv = hasPv ? "pv -n -s " + Math.Max(1, totalBytes) + " " : "";
            var skipOld = tarSkipOldFiles ? " --skip-old-files" : "";
            foreach (var path in req.SourcePaths)
            {
                var parent = ParentOf(path);
                var name = BaseNameOf(path);
                var remoteCmd = "mkdir -p -- " + ShellQuote(req.TargetDirectory) +
                                " && tar -C " + ShellQuote(req.TargetDirectory) + skipOld + " -xf -";
                parts.Add("tar -C " + ShellQuote(parent) + " -cf - -- " + ShellQuote(name) +
                          " | " + pv + auth + sshCmd + " " + destQ + " " + ShellQuote(remoteCmd));
            }
            payload = string.Join(" && ", parts);
        }

        if (!usePassword)
            return payload;

        // 密码：从 stdin 读入 0600 临时文件，sshpass -f 引用于命令行（密码本身不落在 argv）。
        const string bootstrap =
            "umask 077; d=$(mktemp -d 2>/dev/null || mktemp -d -t litssh); " +
            "f=\"$d/pw\"; cat > \"$f\"; chmod 600 \"$f\"; " +
            "trap 'rm -rf \"$d\"' EXIT HUP INT TERM; ";
        return bootstrap + payload;
    }

    public static int? ParseRsyncPercent(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = RsyncPercentRegex.Match(line);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) && p is >= 0 and <= 100 ? p : null;
    }

    public static int? ParsePvPercent(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = PvPercentRegex.Match(line);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) && p is >= 0 and <= 100 ? p : null;
    }
}
