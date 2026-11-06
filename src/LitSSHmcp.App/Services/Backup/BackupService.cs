using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.App.Services.Backup;

public sealed record BackupRunResult(
    bool Success,
    string Message,
    string? LocalPath = null,
    bool Uploaded = false,
    string? UploadMessage = null,
    long Bytes = 0);

public sealed class BackupService
{
    private readonly IBackupUploader _uploader;
    private readonly ISecretProtector _protector;
    private readonly string _dataDir;

    public BackupService(IBackupUploader uploader, ISecretProtector protector, string? dataDir = null)
    {
        _uploader = uploader;
        _protector = protector;
        _dataDir = dataDir ?? ConfigPaths.AppDataDir;
    }

    public async Task<BackupRunResult> RunAsync(BackupPlanConfig plan, AppConfig config,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plan.TargetDirectory))
            return new BackupRunResult(false, "未配置本地备份目录。");

        var target = Path.GetFullPath(plan.TargetDirectory);
        var items = BackupItemCatalog.Build(config, _dataDir, plan.Items)
            .Where(item => item.Exists)
            .ToList();
        if (items.Count == 0)
            return new BackupRunResult(false, "没有可备份的已选项目。");

        var estimatedBytes = items.Sum(item => item.Size);
        progress?.Report("预计备份大小：" + BackupFormat.Size(estimatedBytes));
        try
        {
            var root = Path.GetPathRoot(target);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < estimatedBytes)
                    return new BackupRunResult(false, "目标磁盘空间不足。预计需要 " +
                        BackupFormat.Size(estimatedBytes) + "，可用 " +
                        BackupFormat.Size(drive.AvailableFreeSpace) + "。");
            }
        }
        catch
        {
            // 网络路径等无法读取磁盘空间时跳过检查
        }

        foreach (var item in items)
        {
            if (IsInside(target, item.Path))
                return new BackupRunResult(false,
                    $"备份目录不能位于被备份项目内部：{target} 位于 {item.Path} 内。");
        }

        var backupPassword = plan.EncryptBackup
            ? _protector.Unprotect(plan.BackupPasswordProtected)
            : null;
        if (plan.EncryptBackup && string.IsNullOrEmpty(backupPassword))
            return new BackupRunResult(false, "计划已启用加密备份，但未配置备份密码。");

        var staging = Path.Combine(Path.GetTempPath(),
            "litssh-backup-" + Guid.NewGuid().ToString("N"));
        var fileName = BuildFileName(plan);
        var finalPath = Path.Combine(target, fileName);
        var tempZip = finalPath + ".zip.tmp";
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(staging);
            progress?.Report("正在复制备份项目…");
            var manifest = new BackupManifest
            {
                PlanId = plan.Id,
                PlanName = plan.Name,
                CreatedAt = DateTimeOffset.Now,
                DataDir = _dataDir,
                Encrypted = plan.EncryptBackup,
                Warning = "配置中的 DPAPI 加密项只能在原 Windows 用户下解密；迁移到其他电脑后需要重新填写。",
                Items = new List<BackupManifestItem>()
            };

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.Combine("items", item.Id);
                var destination = Path.Combine(staging, relative);
                if (item.Descriptor.Kind != BackupItemKind.Directory)
                    destination = Path.Combine(destination, Path.GetFileName(item.Path));
                progress?.Report($"正在备份：{item.Name}");
                var copied = await CopyItemAsync(item, destination, ct);
                manifest.Items.Add(new BackupManifestItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    SourcePath = item.Path,
                    DestinationPath = relative.Replace('\\', '/'),
                    Size = copied
                });
            }

            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);

            progress?.Report("正在压缩备份…");
            if (File.Exists(tempZip))
                File.Delete(tempZip);
            await CreateZipAsync(staging, tempZip, ct);
            if (plan.EncryptBackup)
            {
                var encryptedTemp = finalPath + ".enc.tmp";
                await Task.Run(() => BackupCrypto.EncryptFile(tempZip, encryptedTemp, backupPassword!), ct);
                File.Move(encryptedTemp, finalPath, overwrite: true);
                File.Delete(tempZip);
            }
            else
            {
                File.Move(tempZip, finalPath, overwrite: true);
            }
            var bytes = new FileInfo(finalPath).Length;
            PruneLocalBackups(plan, target);

            var uploadMessage = (string?)null;
            var uploaded = false;
            if (plan.UploadToOss)
            {
                progress?.Report("正在上传阿里云 OSS…");
                try
                {
                    var secret = _protector.Unprotect(config.Backup.Oss.AccessKeySecretProtected) ?? string.Empty;
                    await _uploader.UploadAsync(config.Backup.Oss, secret,
                        finalPath, BuildObjectKey(config.Backup.Oss, plan, fileName), ct);
                    uploaded = true;
                    uploadMessage = "OSS 上传成功。";
                }
                catch (Exception ex)
                {
                    uploadMessage = "本地备份成功，OSS 上传失败：" + ex.Message;
                }
            }

            return new BackupRunResult(true,
                uploadMessage ?? "本地备份成功。", finalPath, uploaded, uploadMessage, bytes);
        }
        catch (OperationCanceledException)
        {
            return new BackupRunResult(false, "备份已取消。");
        }
        catch (Exception ex)
        {
            return new BackupRunResult(false, "备份失败：" + ex.Message);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* ignore */ }
            try { if (File.Exists(finalPath + ".enc.tmp")) File.Delete(finalPath + ".enc.tmp"); } catch { /* ignore */ }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* ignore */ }
        }
    }

    public static string BuildObjectKey(BackupOssConfig oss, BackupPlanConfig plan, string fileName)
    {
        var prefix = string.IsNullOrWhiteSpace(plan.OssPrefixOverride)
            ? oss.Prefix
            : plan.OssPrefixOverride;
        prefix = (prefix ?? string.Empty).Trim('/');
        var path = string.IsNullOrWhiteSpace(prefix)
            ? $"{plan.Id}/{DateTime.Now:yyyy/MM}/{fileName}"
            : $"{prefix}/{plan.Id}/{DateTime.Now:yyyy/MM}/{fileName}";
        return path.Replace('\\', '/');
    }

    public static string BuildFileName(BackupPlanConfig plan) =>
        PlanPrefix(plan) + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (plan.EncryptBackup ? ".lsbak" : ".zip");

    private static string PlanPrefix(BackupPlanConfig plan) =>
        "LitSSH-backup-" + SafeName(plan.Name) + "-" + plan.Id[..Math.Min(8, plan.Id.Length)] + "-";

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string((name ?? string.Empty)
            .Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(value) ? "backup" : value;
    }

    private static async Task CreateZipAsync(string staging, string zipPath, CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };
            foreach (var file in Directory.EnumerateFiles(staging, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(staging, file).Replace('\\', '/');
                var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                await using var output = entry.Open();
                await using var input = File.OpenRead(file);
                await input.CopyToAsync(output, ct);
            }
        }, ct);
    }

    private async Task<long> CopyItemAsync(BackupItemInfo item, string destination, CancellationToken ct)
    {
        if (item.Descriptor.Kind == BackupItemKind.SqliteDatabase)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await Task.Run(() => BackupSqlite(item.Path, destination), ct);
            return new FileInfo(destination).Length;
        }

        if (item.Descriptor.Kind == BackupItemKind.File)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = new FileStream(item.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 128 * 1024, useAsync: true);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, 128 * 1024, useAsync: true);
            await source.CopyToAsync(target, ct);
            return target.Length;
        }

        Directory.CreateDirectory(destination);
        var bytes = 0L;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var sourcePath in Directory.EnumerateFiles(item.Path, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(item.Path, sourcePath);
            var targetPath = Path.Combine(destination, relative);
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 128 * 1024, useAsync: true);
            await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 128 * 1024, useAsync: true);
            await source.CopyToAsync(target, ct);
            bytes += target.Length;
        }
        return bytes;
    }

    private static void BackupSqlite(string sourcePath, string destinationPath)
    {
        using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
        using var target = new SqliteConnection($"Data Source={destinationPath};Pooling=False");
        source.Open();
        target.Open();
        source.BackupDatabase(target);
        target.Close();
    }

    private void PruneLocalBackups(BackupPlanConfig plan, string target)
    {
        if (plan.KeepLocalCopies <= 0)
            return;
        try
        {
            var prefix = PlanPrefix(plan);
            var files = Directory.GetFiles(target, prefix + "*")
                .Where(path => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".lsbak", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Skip(plan.KeepLocalCopies)
                .ToArray();
            foreach (var file in files)
            {
                try { File.Delete(file); } catch { /* ignore */ }
            }
        }
        catch
        {
            // 保留策略失败不影响备份结果
        }
    }

    private static bool IsInside(string path, string parent)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }
}
