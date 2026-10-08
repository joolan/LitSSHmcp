using System.Diagnostics;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace LitSSHmcp.Core.Services.Sync;

public sealed record SyncProgress(string Message, int Done, int Total, long Bytes);

public sealed class SyncResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int FilesTransferred { get; init; }
    public long BytesTransferred { get; init; }
    public int Deleted { get; init; }
    public TimeSpan Duration { get; init; }
    /// <summary>是否可通过重试恢复（网络/超时等瞬时错误）。</summary>
    public bool Retryable { get; init; }
}

public sealed class SyncTestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 单向文件/文件夹同步引擎（自研，基于 SFTP）：比对源/目标（size + mtime），仅传输变更文件；
/// 可选镜像删除目标端多余文件。远程↔远程经本机中转（P1；后续可优化为服务器端 rsync 直连）。
/// </summary>
public sealed class SyncService
{
    private readonly ISshKnownHostsStore? _knownHosts;
    private readonly SshHostKeyMode _hostKeyMode;
    private readonly ISshService? _ssh;

    public SyncService(ISshKnownHostsStore? knownHosts = null, SshHostKeyMode hostKeyMode = SshHostKeyMode.Tofu,
        ISshService? ssh = null)
    {
        _knownHosts = knownHosts;
        _hostKeyMode = hostKeyMode;
        _ssh = ssh;
    }

    public Task<SyncResult> RunAsync(SyncTaskConfig task, AppConfig config,
        IProgress<SyncProgress>? progress, CancellationToken ct)
        => Task.Run(() => RunCore(task, config, progress, ct), ct);

    public Task<SyncTestResult> TestAsync(SyncTaskConfig task, AppConfig config, CancellationToken ct)
        => Task.Run(() => TestCore(task, config, ct), ct);

    // ---------- 运行 ----------

    private SyncResult RunCore(SyncTaskConfig task, AppConfig config, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return task.Direction switch
            {
                SyncDirection.LocalToRemote => SyncLocalRemote(task, config, upload: true, progress, ct, sw),
                SyncDirection.RemoteToLocal => SyncLocalRemote(task, config, upload: false, progress, ct, sw),
                SyncDirection.RemoteToRemote => SyncRemoteRemote(task, config, progress, ct, sw),
                _ => Fail("未知同步方向", sw, retryable: false)
            };
        }
        catch (OperationCanceledException)
        {
            return Fail("已取消", sw, retryable: false);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, sw, retryable: true);
        }
    }

    private SyncResult SyncLocalRemote(SyncTaskConfig task, AppConfig config, bool upload,
        IProgress<SyncProgress>? progress, CancellationToken ct, Stopwatch sw)
    {
        var serverId = upload ? task.TargetServerId : task.SourceServerId;
        var server = ResolveServer(config, serverId, upload ? "目标" : "源");
        var localRoot = Require(task.LocalPath, "本地路径");
        var remoteRoot = Require(upload ? task.TargetRemotePath : task.SourceRemotePath, "远程路径");

        using var sftp = OpenSftp(server);

        var source = ApplyFilters(upload ? ListLocal(localRoot) : ListRemote(sftp, remoteRoot), task);
        var target = upload ? ListRemote(sftp, remoteRoot) : ListLocal(localRoot);

        var toTransfer = source.Where(kv => IsChanged(kv.Value, target.GetValueOrDefault(kv.Key))).ToList();
        progress?.Report(new SyncProgress($"待传输 {toTransfer.Count} 个文件", 0, toTransfer.Count, 0));

        var total = toTransfer.Count;
        var done = 0;
        long bytes = 0;
        foreach (var (rel, meta) in toTransfer)
        {
            ct.ThrowIfCancellationRequested();
            var remotePath = CombineRemote(remoteRoot, rel);
            var localPath = Path.Combine(Path.GetFullPath(localRoot), rel.Replace('/', Path.DirectorySeparatorChar));
            if (upload)
                UploadFile(sftp, localPath, remotePath);
            else
                DownloadFile(sftp, remotePath, localPath);
            done++;
            bytes += meta.Size;
            progress?.Report(new SyncProgress($"传输 {rel}", done, total, bytes));
        }

        var deleted = 0;
        if (task.DeleteExtra)
        {
            foreach (var rel in target.Keys)
            {
                ct.ThrowIfCancellationRequested();
                if (source.ContainsKey(rel))
                    continue;
                if (upload)
                    TryDeleteRemoteFile(sftp, CombineRemote(remoteRoot, rel));
                else
                    TryDeleteLocalFile(Path.Combine(Path.GetFullPath(localRoot), rel.Replace('/', Path.DirectorySeparatorChar)));
                deleted++;
            }
        }

        sw.Stop();
        return new SyncResult
        {
            Success = true,
            Message = $"完成：传输 {done} 个文件, 删除 {deleted} 个",
            FilesTransferred = done,
            BytesTransferred = bytes,
            Deleted = deleted,
            Duration = sw.Elapsed
        };
    }

    private SyncResult SyncRemoteRemote(SyncTaskConfig task, AppConfig config,
        IProgress<SyncProgress>? progress, CancellationToken ct, Stopwatch sw)
    {
        if (_ssh is null)
            return Fail("未初始化 SSH 服务，无法进行服务器端同步。", sw, retryable: false);

        var sourceServer = ResolveServer(config, task.SourceServerId, "源");
        var targetServer = ResolveServer(config, task.TargetServerId, "目标");
        var srcRoot = Require(task.SourceRemotePath, "源远程路径");
        var dstRoot = Require(task.TargetRemotePath, "目标远程路径");

        var (tHost, tPort) = RemoteCopyCommandBuilder.ResolveEndpoint(targetServer, task.UseInternalAddress);
        var (ok, error) = CheckRemoteRemote(sourceServer, targetServer, tHost, tPort, out var usePassword);
        if (!ok)
            return Fail(error!, sw, retryable: false);

        progress?.Report(new SyncProgress("服务器端 rsync 同步中…", 0, 0, 0));
        var cmd = RemoteCopyCommandBuilder.BuildRsyncSyncCommand(
            srcRoot.TrimEnd('/') + "/", tHost, tPort, targetServer.Username, dstRoot,
            task.DeleteExtra, usePassword, task.IncludePatterns, task.ExcludePatterns);

        var exec = _ssh.ExecuteStreamingAsync(sourceServer, cmd, null, null,
            usePassword ? targetServer.Password : null, ct, 3600).GetAwaiter().GetResult();

        sw.Stop();
        if (exec.Success)
            return new SyncResult { Success = true, Message = "服务器端 rsync 同步完成", Duration = sw.Elapsed };

        var msg = string.IsNullOrWhiteSpace(exec.Error) ? $"rsync 失败（退出码 {exec.ExitCode}）" : exec.Error.Trim();
        return new SyncResult { Success = false, Message = msg, Duration = sw.Elapsed, Retryable = true };
    }

    /// <summary>探测服务器端同步前提：源/目标两端均需 rsync；认证优先源机免密，否则 sshpass+目标密码。</summary>
    private (bool Ok, string? Error) CheckRemoteRemote(SshServerConfig source, SshServerConfig target,
        string targetHost, int targetPort, out bool usePassword)
    {
        usePassword = false;

        var srcProbe = ExecRemote(source, RemoteCopyCommandBuilder.BuildProbeCommand(targetHost, targetPort, target.Username));
        var hasRsyncSrc = srcProbe.Contains("LITSSH_HAS_RSYNC", StringComparison.Ordinal);
        var hasSshpass = srcProbe.Contains("LITSSH_HAS_SSHPASS", StringComparison.Ordinal);
        var keyOk = srcProbe.Contains("LITSSH_KEY_OK", StringComparison.Ordinal);

        var tgtProbe = ExecRemote(target, RemoteCopyCommandBuilder.BuildTargetProbeCommand());
        var hasRsyncTgt = tgtProbe.Contains("LITSSH_T_RSYNC", StringComparison.Ordinal);

        var missing = new List<string>();
        if (!hasRsyncSrc)
            missing.Add($"源服务器「{source.Name}」缺少 rsync：{InstallHint("rsync", PackageManagers(source))}");
        if (!hasRsyncTgt)
            missing.Add($"目标服务器「{target.Name}」缺少 rsync：{InstallHint("rsync", PackageManagers(target))}");
        if (missing.Count > 0)
            return (false, "服务器端同步需要**源/目标两端都安装 rsync**：\n" + string.Join("\n", missing));

        if (keyOk)
        {
            usePassword = false;
        }
        else if (hasSshpass && !string.IsNullOrEmpty(target.Password))
        {
            usePassword = true;
        }
        else
        {
            return (false, "源服务器未能免密连接目标，且无法使用密码（源机缺 sshpass 或目标未配密码）。\n\n" +
                           $"【方式 A · 推荐】在源服务器「{source.Name}」上执行以下命令配置到目标的免密（仅需一次，需输入目标密码）：\n" +
                           PasswordlessHint(targetHost, targetPort, target.Username) + "\n\n" +
                           "【方式 B】在源服务器「" + source.Name + "」上安装 sshpass，" +
                           "并在本工具中为「目标」服务器「" + target.Name + "」配置登录密码：" +
                           InstallHint("sshpass", PackageManagers(source)));
        }

        return (true, null);
    }

    /// <summary>生成在源机配置到目标免密的命令（供测试失败提示复制执行）。</summary>
    private static string PasswordlessHint(string host, int port, string user)
    {
        var target = $"{user}@{host}";
        return
            $"  ssh-keygen -t ed25519 -N \"\" -f ~/.ssh/id_ed25519   # 1) 生成密钥（已有可跳过）\n" +
            $"  ssh-copy-id -p {port} {target}                     # 2) 安装公钥到目标（输入目标密码一次）\n" +
            $"  ssh -o BatchMode=yes -p {port} {target} true && echo OK   # 3) 验证免密是否成功";
    }

    private string ExecRemote(SshServerConfig server, string command)
        => _ssh!.ExecuteCommandAsync(server, command, default, 30).GetAwaiter().GetResult().Output ?? string.Empty;

    private List<string> PackageManagers(SshServerConfig server)
    {
        var output = ExecRemote(server, "for c in apt-get dnf yum zypper pacman apk; do command -v $c >/dev/null 2>&1 && echo HAS:$c; done");
        return output.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("HAS:", StringComparison.Ordinal))
            .Select(l => l[4..].Trim())
            .Where(l => l.Length > 0)
            .Distinct()
            .ToList();
    }

    private static string InstallHint(string tool, IReadOnlyList<string> packageManagers)
    {
        if (packageManagers.Count == 0)
            return $"（未识别包管理器，请用对应发行版命令安装 {tool}）";

        var lines = new List<string>();
        foreach (var pm in packageManagers)
        {
            lines.Add(pm switch
            {
                "apt-get" => $"sudo apt-get update && sudo apt-get install -y {tool}",
                "dnf" => $"sudo dnf install -y {tool}",
                "yum" => $"sudo yum install -y epel-release && sudo yum install -y {tool}",
                "zypper" => $"sudo zypper install -y {tool}",
                "pacman" => $"sudo pacman -S --noconfirm {tool}",
                "apk" => $"sudo apk add --no-cache {tool}",
                _ => $"安装 {tool}"
            });
        }
        return string.Join("  或  ", lines);
    }

    // ---------- 探针测试（必须实际传一个小文件） ----------

    private SyncTestResult TestCore(SyncTaskConfig task, AppConfig config, CancellationToken ct)
    {
        try
        {
            var payload = new byte[512];
            Random.Shared.NextBytes(payload);
            const string probeName = ".litsshsync.probe";

            switch (task.Direction)
            {
                case SyncDirection.LocalToRemote:
                {
                    var server = ResolveServer(config, task.TargetServerId, "目标");
                    var remoteRoot = Require(task.TargetRemotePath, "目标远程路径");
                    using var sftp = OpenSftp(server);
                    var remoteProbe = CombineRemote(remoteRoot, probeName);
                    WriteRemoteBytes(sftp, remoteProbe, payload);
                    var ok = ReadRemoteBytes(sftp, remoteProbe, payload.Length) != null;
                    TryDeleteRemoteFile(sftp, remoteProbe);
                    return ok ? Ok("探针文件上传成功") : Err("探针文件校验失败");
                }
                case SyncDirection.RemoteToLocal:
                {
                    var server = ResolveServer(config, task.SourceServerId, "源");
                    var remoteRoot = Require(task.SourceRemotePath, "源远程路径");
                    var localRoot = Require(task.LocalPath, "本地路径");
                    Directory.CreateDirectory(localRoot);
                    using var sftp = OpenSftp(server);
                    var remoteProbe = CombineRemote(remoteRoot, probeName);
                    WriteRemoteBytes(sftp, remoteProbe, payload);
                    var localProbe = Path.Combine(Path.GetFullPath(localRoot), probeName + ".test");
                    using (var fs = File.Create(localProbe)) sftp.DownloadFile(remoteProbe, fs);
                    var ok = new FileInfo(localProbe).Length == payload.Length;
                    TryDeleteRemoteFile(sftp, remoteProbe);
                    TryDeleteLocalFile(localProbe);
                    return ok ? Ok("探针文件下载成功") : Err("探针文件校验失败");
                }
                default:
                {
                    if (_ssh is null)
                        return Err("未初始化 SSH 服务，无法进行服务器端同步测试。");
                    var sourceServer = ResolveServer(config, task.SourceServerId, "源");
                    var targetServer = ResolveServer(config, task.TargetServerId, "目标");
                    var srcRoot = Require(task.SourceRemotePath, "源远程路径");
                    var dstRoot = Require(task.TargetRemotePath, "目标远程路径");

                    // 前置条件：源/目标两端都需 rsync（缺失时提示对应端的安装命令），并确认认证方式
                    var (tHost, tPort) = RemoteCopyCommandBuilder.ResolveEndpoint(targetServer, task.UseInternalAddress);
                    var (preOk, preError) = CheckRemoteRemote(sourceServer, targetServer, tHost, tPort, out var usePassword);
                    if (!preOk)
                        return Err(preError!);

                    var srcProbe = CombineRemote(srcRoot, probeName);
                    using (var src = OpenSftp(sourceServer))
                        WriteRemoteBytes(src, srcProbe, payload);

                    var cmd = RemoteCopyCommandBuilder.BuildRsyncSyncCommand(
                        srcProbe, tHost, tPort, targetServer.Username, dstRoot,
                        deleteExtra: false, usePassword, task.IncludePatterns, task.ExcludePatterns);
                    var exec = _ssh.ExecuteStreamingAsync(sourceServer, cmd, null, null,
                        usePassword ? targetServer.Password : null, ct, 120).GetAwaiter().GetResult();
                    if (!exec.Success)
                    {
                        var detail = string.IsNullOrWhiteSpace(exec.Error) ? exec.Output : exec.Error;
                        return Err("探针文件服务器间 rsync 传输失败: " + detail);
                    }

                    var dstProbe = CombineRemote(dstRoot, probeName);
                    bool ok;
                    using (var dst = OpenSftp(targetServer))
                    {
                        var back = ReadRemoteBytes(dst, dstProbe, payload.Length);
                        ok = back != null && back.Length == payload.Length;
                        TryDeleteRemoteFile(dst, dstProbe);
                    }
                    using (var src = OpenSftp(sourceServer))
                        TryDeleteRemoteFile(src, srcProbe);

                    return ok ? Ok("探针文件服务器间 rsync 传输成功") : Err("探针文件校验失败");
                }
            }
        }
        catch (Exception ex)
        {
            return Err("测试失败: " + ex.Message);
        }
    }

    // ---------- 基础工具 ----------

    private static bool IsChanged((long Size, DateTime Mtime) src, (long Size, DateTime Mtime) dst)
    {
        if (dst.Size == 0 && dst.Mtime == default)
            return true; // 目标不存在
        if (src.Size != dst.Size)
            return true;
        return Math.Abs((src.Mtime - dst.Mtime).TotalSeconds) > 2;
    }

    /// <summary>按 include/exclude（glob）过滤文件集合（相对路径）。</summary>
    private static Dictionary<string, (long Size, DateTime Mtime)> ApplyFilters(
        Dictionary<string, (long Size, DateTime Mtime)> files, SyncTaskConfig task)
        => SyncFileFilter.Apply(files, task);

    private static SshServerConfig ResolveServer(AppConfig config, string? serverId, string role)
    {
        if (string.IsNullOrWhiteSpace(serverId))
            throw new InvalidOperationException($"未配置{role}服务器");
        // 允许使用已禁用的服务器（禁用仅表示不对 MCP/AI 暴露，不影响本地同步）
        return config.Servers.FirstOrDefault(s => s.Id == serverId)
            ?? throw new InvalidOperationException($"{role}服务器不存在: {serverId}");
    }

    private static string Require(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"未配置{name}") : value.Trim();

    private SftpClient OpenSftp(SshServerConfig server)
    {
        var client = SshClientFactory.CreateSftp(server, _knownHosts, _hostKeyMode);
        client.Connect();
        return client;
    }

    private static Dictionary<string, (long Size, DateTime Mtime)> ListLocal(string root)
    {
        var dict = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
            return dict;
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(full, file).Replace('\\', '/');
            var info = new FileInfo(file);
            dict[rel] = (info.Length, info.LastWriteTimeUtc);
        }
        return dict;
    }

    private static Dictionary<string, (long Size, DateTime Mtime)> ListRemote(SftpClient sftp, string root)
    {
        var dict = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
        var rootNorm = NormalizeRemote(root);
        var stack = new Stack<string>();
        stack.Push(rootNorm);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            ICollection<ISftpFile> entries;
            try { entries = sftp.ListDirectory(dir).ToList(); }
            catch { continue; }
            foreach (var e in entries)
            {
                if (e.Name is "." or "..") continue;
                if (e.IsSymbolicLink) continue;
                if (e.IsDirectory)
                {
                    stack.Push(e.FullName);
                }
                else
                {
                    var rel = e.FullName.StartsWith(rootNorm, StringComparison.Ordinal)
                        ? e.FullName[rootNorm.Length..].TrimStart('/')
                        : e.Name;
                    dict[rel] = (e.Length, e.LastWriteTimeUtc);
                }
            }
        }
        return dict;
    }

    private static void UploadFile(SftpClient sftp, string localFile, string remoteFile)
    {
        EnsureRemoteDir(sftp, RemoteDirOf(remoteFile));
        var tmp = remoteFile + ".litsshsync.tmp";
        using (var fs = File.OpenRead(localFile))
            sftp.UploadFile(fs, tmp, true);
        if (sftp.Exists(remoteFile)) sftp.DeleteFile(remoteFile);
        sftp.RenameFile(tmp, remoteFile);
    }

    private static void DownloadFile(SftpClient sftp, string remoteFile, string localFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        var tmp = localFile + ".litsshsync.tmp";
        using (var fs = File.Create(tmp))
            sftp.DownloadFile(remoteFile, fs);
        if (File.Exists(localFile)) File.Delete(localFile);
        File.Move(tmp, localFile);
    }

    private static void RelayRemoteFile(SftpClient src, string srcFile, SftpClient dst, string dstFile)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "litsshsync", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        try
        {
            using (var fs = File.Create(tmp))
                src.DownloadFile(srcFile, fs);
            using (var fs = File.OpenRead(tmp))
            {
                EnsureRemoteDir(dst, RemoteDirOf(dstFile));
                var dtmp = dstFile + ".litsshsync.tmp";
                dst.UploadFile(fs, dtmp, true);
                if (dst.Exists(dstFile)) dst.DeleteFile(dstFile);
                dst.RenameFile(dtmp, dstFile);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略 */ }
        }
    }

    private static void EnsureRemoteDir(SftpClient sftp, string dir)
    {
        var normalized = NormalizeRemote(dir);
        if (normalized.Length == 0 || normalized == "/")
            return;
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cur = normalized.StartsWith('/') ? string.Empty : string.Empty;
        foreach (var part in parts)
        {
            cur += "/" + part;
            try { if (!sftp.Exists(cur)) sftp.CreateDirectory(cur); }
            catch { /* 已存在/并发创建 */ }
        }
    }

    private static void TryDeleteRemoteFile(SftpClient sftp, string path)
    {
        try { if (sftp.Exists(path)) sftp.DeleteFile(path); }
        catch { /* 忽略 */ }
    }

    private static void TryDeleteLocalFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 忽略 */ }
    }

    private static void WriteRemoteBytes(SftpClient sftp, string path, byte[] data)
    {
        EnsureRemoteDir(sftp, RemoteDirOf(path));
        using var stream = sftp.OpenWrite(path);
        stream.Write(data, 0, data.Length);
    }

    private static byte[]? ReadRemoteBytes(SftpClient sftp, string path, int expected)
    {
        try
        {
            using var stream = sftp.OpenRead(path);
            var buffer = new byte[expected];
            var read = 0;
            while (read < expected)
            {
                var n = stream.Read(buffer, read, expected - read);
                if (n <= 0) break;
                read += n;
            }
            return buffer[..read];
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeRemote(string path)
    {
        var p = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    private static string CombineRemote(string root, string relative)
    {
        var r = NormalizeRemote(root);
        var rel = (relative ?? string.Empty).Replace('\\', '/').TrimStart('/');
        return rel.Length == 0 ? r : (r == "/" ? "/" + rel : r + "/" + rel);
    }

    private static string RemoteDirOf(string file)
    {
        var idx = file.Replace('\\', '/').LastIndexOf('/');
        return idx <= 0 ? "/" : file[..idx];
    }

    private static SyncResult Fail(string message, Stopwatch sw, bool retryable)
    {
        sw.Stop();
        return new SyncResult { Success = false, Message = message, Duration = sw.Elapsed, Retryable = retryable };
    }

    private static SyncTestResult Ok(string message) => new() { Success = true, Message = message };
    private static SyncTestResult Err(string message) => new() { Success = false, Message = message };
}
