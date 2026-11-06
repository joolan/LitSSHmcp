using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LitSSHmcp.App.Services.Backup;

public sealed class BackupArchive : IDisposable
{
    private readonly bool _ownsZip;
    internal BackupArchive(string zipPath, bool ownsZip, BackupManifest manifest, int entryCount)
    {
        ZipPath = zipPath;
        _ownsZip = ownsZip;
        Manifest = manifest;
        EntryCount = entryCount;
    }

    public string ZipPath { get; }
    public BackupManifest Manifest { get; }
    public int EntryCount { get; }

    public void Dispose()
    {
        if (!_ownsZip)
            return;
        try { File.Delete(ZipPath); } catch { /* ignore */ }
    }
}

public sealed record BackupVerifyResult(bool Success, string Message, string? Sha256 = null, int EntryCount = 0);

public sealed record BackupRestoreResult(bool Success, string Message, int RestoredFiles = 0, string? SafetyDirectory = null);

public sealed class BackupRestoreService
{
    public async Task<BackupArchive> OpenAsync(string path, string? password,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("备份文件不存在。", path);
        var encrypted = BackupCrypto.IsEncrypted(path);
        var zipPath = path;
        var ownsZip = false;
        if (encrypted)
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("该备份已加密，请输入备份密码。");
            zipPath = Path.Combine(Path.GetTempPath(),
                "litssh-restore-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                await Task.Run(() => BackupCrypto.DecryptFile(path, zipPath, password, ct), ct);
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
            {
                try { File.Delete(zipPath); } catch { /* ignore */ }
                throw new InvalidOperationException("备份密码错误或文件损坏。", ex);
            }
            ownsZip = true;
        }

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("备份缺少 manifest.json。");
            await using var stream = entry.Open();
            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: ct)
                ?? throw new InvalidDataException("manifest.json 无法解析。");
            return new BackupArchive(zipPath, ownsZip, manifest, archive.Entries.Count);
        }
        catch
        {
            if (ownsZip)
            {
                try { File.Delete(zipPath); } catch { /* ignore */ }
            }
            throw;
        }
    }

    public async Task<int> RestoreToDirectoryAsync(BackupArchive archive, string targetDirectory,
        IReadOnlyCollection<string> itemIds, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var targetRoot = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(targetRoot);
        using var zip = ZipFile.OpenRead(archive.ZipPath);
        var restored = 0;
        foreach (var item in archive.Manifest.Items.Where(item => itemIds.Contains(item.Id)))
        {
            ct.ThrowIfCancellationRequested();
            var prefix = "items/" + item.Id + "/";
            var entries = zip.Entries
                .Where(entry => entry.FullName == "items/" + item.Id ||
                                entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            if (entries.Length == 0)
                continue;

            progress?.Report("正在恢复：" + item.Name);
            foreach (var entry in entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;
                var relative = entry.FullName["items/".Length..];
                var destination = Path.GetFullPath(Path.Combine(targetRoot, relative));
                if (!IsInside(destination, targetRoot))
                    continue;
                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                entry.ExtractToFile(destination, overwrite: true);
                restored++;
            }
        }
        await Task.CompletedTask;
        return restored;
    }

    public async Task<BackupRestoreResult> RestoreToDataDirectoryAsync(BackupArchive archive,
        LitSSHmcp.Core.Models.AppConfig config, string dataDir,
        IReadOnlyCollection<string> itemIds, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var staging = Path.Combine(Path.GetTempPath(),
            "litssh-restore-items-" + Guid.NewGuid().ToString("N"));
        var safety = Path.Combine(dataDir, "restore-safety-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        var processed = new List<(BackupItemInfo Item, bool Existed)>();
        try
        {
            Directory.CreateDirectory(staging);
            await RestoreToDirectoryAsync(archive, staging, itemIds, progress, ct);
            var items = BackupItemCatalog.Build(config, dataDir, itemIds);
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var staged = item.Descriptor.Kind == BackupItemKind.Directory
                    ? Path.Combine(staging, item.Id)
                    : Path.Combine(staging, item.Id, Path.GetFileName(item.Path));
                if (!File.Exists(staged) && !Directory.Exists(staged))
                    continue;

                var existed = File.Exists(item.Path) || Directory.Exists(item.Path);
                if (existed)
                {
                    var safetyPath = Path.Combine(safety, item.Id);
                    CopyPath(item.Path, safetyPath);
                }
                processed.Add((item, existed));
                ReplacePath(staged, item.Path, item.Descriptor.Kind == BackupItemKind.Directory);
                progress?.Report("已恢复：" + item.Name);
            }
            return new BackupRestoreResult(true, "恢复完成，请重启 App 使数据库和配置生效。",
                processed.Count, Directory.Exists(safety) ? safety : null);
        }
        catch (Exception ex)
        {
            foreach (var (item, existed) in processed.AsEnumerable().Reverse())
            {
                try
                {
                    DeletePath(item.Path);
                    if (existed)
                    {
                        var safetyPath = Path.Combine(safety, item.Id);
                        if (File.Exists(safetyPath) || Directory.Exists(safetyPath))
                            CopyPath(safetyPath, item.Path);
                    }
                }
                catch { /* 回滚失败时保留安全目录供人工处理 */ }
            }
            return new BackupRestoreResult(false, "恢复失败：" + ex.Message +
                (Directory.Exists(safety) ? "；安全备份位于 " + safety : string.Empty));
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
        }
    }

    public async Task<BackupVerifyResult> VerifyAsync(string path, string? password,
        CancellationToken ct = default)
    {
        try
        {
            using var archive = await OpenAsync(path, password, ct);
            using var zip = ZipFile.OpenRead(archive.ZipPath);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                await using var stream = entry.Open();
                var buffer = new byte[81920];
                while (await stream.ReadAsync(buffer, ct) > 0)
                {
                }
            }
            var hash = await Task.Run(() => ComputeSha256(path), ct);
            return new BackupVerifyResult(true, "完整性校验通过。", hash, archive.EntryCount);
        }
        catch (Exception ex)
        {
            return new BackupVerifyResult(false, ex.Message);
        }
    }

    private static void ReplacePath(string source, string destination, bool directory)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        if (!directory)
        {
            File.Copy(source, destination, overwrite: true);
            return;
        }

        DeletePath(destination);
        try { Directory.Move(source, destination); }
        catch
        {
            CopyPath(source, destination);
        }
    }

    private static void CopyPath(string source, string destination)
    {
        if (File.Exists(source))
        {
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.Copy(source, destination, overwrite: true);
            return;
        }
        if (!Directory.Exists(source))
            return;
        Directory.CreateDirectory(destination);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void DeletePath(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // 文件可能被占用；由调用方决定是否回滚
            throw;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static bool IsInside(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
