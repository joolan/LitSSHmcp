using System.Diagnostics;
using System.IO.Compression;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Trigger;

/// <summary>
/// 目录备份动作：在触发目标上把 <see cref="TriggerAction.SourcePath"/> 打包到 <see cref="TriggerAction.DestDir"/>，
/// 文件名 {触发事件名}-{yyyyMMdd-HHmmss}.{ext}；完成后按 RetentionCount 清理最旧备份。
/// 本机优先 Zip（System.IO.Compression），TarGz 走 tar.exe（不可用时回退 Zip）；远程优先 tar，缺失时回退 zip。
/// </summary>
public static class BackupAction
{
    private const int TimeoutSeconds = 600;

    public static async Task<TriggerActionResult> RunAsync(TriggerActionContext ctx, CancellationToken ct = default)
    {
        var action = ctx.Action;
        var sw = Stopwatch.StartNew();
        var result = new TriggerActionResult { Type = TriggerActionType.Backup };

        var src = action.SourcePath.TrimEnd('/', '\\');
        var destDir = action.DestDir.TrimEnd('/', '\\');
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(destDir))
        {
            result.Error = "备份动作缺少源目录或输出目录";
            return Finish(result, sw);
        }

        var wantTar = action.BackupFormat == TriggerBackupFormat.TarGz;
        var fileName = $"{SafeName(ctx.Trigger.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.{(wantTar ? "tar.gz" : "zip")}";

        var outcome = ctx.Server is null
            ? await RunLocalAsync(src, destDir, fileName, wantTar, ct)
            : await RunRemoteAsync(ctx, src, destDir, fileName, wantTar, ct);

        result.Success = outcome.Success;
        if (result.Success)
        {
            var saved = await CleanupAsync(ctx, destDir, wantTar ? "tar.gz" : "zip", action.RetentionCount, ct);
            result.Output = outcome.Output + (string.IsNullOrWhiteSpace(saved) ? string.Empty : "；" + saved);
        }
        else
        {
            result.Error = outcome.Error;
        }

        return Finish(result, sw);
    }

    private static TriggerActionResult Finish(TriggerActionResult result, Stopwatch sw)
    {
        result.Duration = sw.Elapsed;
        return result;
    }

    // ---------- 本机 ----------

    private static async Task<(bool Success, string Output, string Error)> RunLocalAsync(
        string src, string destDir, string fileName, bool wantTar, CancellationToken ct)
    {
        if (!Directory.Exists(src))
            return (false, string.Empty, $"源目录不存在: {src}");
        if (IsInside(src, destDir))
            return (false, string.Empty, "备份输出目录不能位于源目录内（否则备份会包含自身）");

        try
        {
            Directory.CreateDirectory(destDir);
            var outFile = Path.Combine(destDir, fileName);

            if (wantTar && await LocalProcessRunner.ExistsAsync("tar", ct))
            {
                var parent = Path.GetDirectoryName(src.TrimEnd(Path.DirectorySeparatorChar)) ?? src;
                var baseName = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar));
                var tar = await LocalProcessRunner.RunAsync("tar",
                    new[] { "-czf", outFile, "-C", parent, baseName }, TimeoutSeconds, ct);
                if (tar.Success)
                    return (true, $"已备份到 {outFile}", string.Empty);
                // tar 失败回退 Zip（tar 存在但不支持等场景）
            }

            var path = outFile.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                ? outFile[..^".tar.gz".Length] + ".zip"   // 回退时改扩展名
                : outFile;
            await Task.Run(() =>
            {
                if (File.Exists(path)) File.Delete(path);
                ZipFile.CreateFromDirectory(src, path, CompressionLevel.Optimal, includeBaseDirectory: true);
            }, ct);
            var size = new FileInfo(path).Length;
            return (true, $"已备份到 {path}（{FormatSize(size)}）", string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, string.Empty, "已取消");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    // ---------- 远程 ----------

    private static async Task<(bool Success, string Output, string Error)> RunRemoteAsync(
        TriggerActionContext ctx, string src, string destDir, string fileName, bool wantTar, CancellationToken ct)
    {
        var server = ctx.Server!;
        var ssh = ctx.Ssh!;
        var q = GitAction.ShellQuote;

        try
        {
            // 源目录存在性
            var probe = await ssh.ExecuteCommandAsync(server, $"test -d {q(src)}", ct, timeoutSeconds: 30);
            if (!probe.Success)
                return (false, string.Empty, $"源目录不存在或不可访问: {src}");

            var hasTar = (await ssh.ExecuteCommandAsync(server, "command -v tar >/dev/null 2>&1", ct, timeoutSeconds: 30)).Success;
            var outFile = $"{destDir}/{fileName}";
            var parent = src.Contains('/') ? src[..src.LastIndexOf('/')] : ".";
            var baseName = src[(src.LastIndexOf('/') + 1)..];
            if (parent.Length == 0) parent = "/";

            if (wantTar && hasTar)
            {
                var cmd = $"mkdir -p {q(destDir)} && tar -czf {q(outFile)} -C {q(parent)} {q(baseName)}";
                var tar = await ssh.ExecuteCommandAsync(server, cmd, ct, timeoutSeconds: TimeoutSeconds);
                if (tar.Success)
                    return (true, $"已备份到 {outFile}", string.Empty);
                // tar 失败继续尝试 zip 回退
                if (ct.IsCancellationRequested)
                    return (false, string.Empty, "已取消");
            }

            // zip 回退
            var hasZip = (await ssh.ExecuteCommandAsync(server, "command -v zip >/dev/null 2>&1", ct, timeoutSeconds: 30)).Success;
            if (!hasZip)
                return (false, string.Empty, wantTar && !hasTar
                    ? "服务器缺少 tar，且未安装 zip"
                    : "备份失败：tar 不可用且服务器缺少 zip");

            var zipName = fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".tar.gz".Length] + ".zip"
                : fileName;
            var zipPath = $"{destDir}/{zipName}";
            var zipCmd = $"mkdir -p {q(destDir)} && cd {q(parent)} && zip -rq {q(zipPath)} {q(baseName)}";
            var zip = await ssh.ExecuteCommandAsync(server, zipCmd, ct, timeoutSeconds: TimeoutSeconds);
            if (!zip.Success)
                return (false, string.Empty, $"zip 失败: {(string.IsNullOrWhiteSpace(zip.Error) ? zip.Output : zip.Error)}");

            return (true, $"已备份到 {zipPath}", string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, string.Empty, "已取消");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    // ---------- 保留份数清理 ----------

    private static async Task<string> CleanupAsync(TriggerActionContext ctx, string destDir, string ext,
        int retentionCount, CancellationToken ct)
    {
        if (retentionCount <= 0)
            return string.Empty;

        var prefix = SafeName(ctx.Trigger.Name);
        try
        {
            if (ctx.Server is null)
            {
                var pattern = $"{prefix}-*.{ext}";
                var files = Directory.Exists(destDir)
                    ? Directory.GetFiles(destDir, pattern).OrderBy(f => f, StringComparer.Ordinal).ToList()
                    : new List<string>();
                if (files.Count <= retentionCount)
                    return string.Empty;

                var removed = 0;
                foreach (var old in files.Take(files.Count - retentionCount))
                {
                    File.Delete(old);
                    removed++;
                }
                return $"已清理 {removed} 份旧备份";
            }

            var q = GitAction.ShellQuote;
            // 按 mtime 倒序保留最新 N 份；glob 部分不加引号以便展开
            var cmd = $"cd {q(destDir)} && ls -1t -- {q(prefix)}-*.{ext} 2>/dev/null | tail -n +{retentionCount + 1} | xargs -r -I{{}} rm -f -- {{}}";
            var r = await ctx.Ssh!.ExecuteCommandAsync(ctx.Server!, cmd, ct, timeoutSeconds: 60);
            return r.Success ? string.Empty : string.Empty; // 清理失败不视为整体失败
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------- 工具 ----------

    /// <summary>dest 是否位于 src 内部（本机会把自身打包进去）。</summary>
    private static bool IsInside(string src, string dest)
    {
        try
        {
            var s = Path.GetFullPath(src).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var d = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return d.StartsWith(s, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '\'', '"' }).ToArray();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim('-');
        return string.IsNullOrEmpty(safe) ? "backup" : safe;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
