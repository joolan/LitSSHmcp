using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.Toolbox;

public sealed record VaultDescriptor(string Id, string Name, string DirectoryPath, DateTimeOffset CreatedAt);

public sealed class VaultMetadata
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "我的文件柜";
    public string Kdf { get; set; } = "PBKDF2-SHA256";
    public int Iterations { get; set; } = 600_000;
    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public byte[] WrappedKey { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class VaultEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;
    public byte[] WrappedDataKey { get; set; } = Array.Empty<byte>();
}

public sealed class VaultIndex
{
    public int Version { get; set; } = 1;
    public List<VaultEntry> Entries { get; set; } = new();
}

public sealed class VaultUnlockException : Exception
{
    public VaultUnlockException(string message) : base(message) { }
}

public sealed record VaultProgress(
    int TotalFiles,
    int CompletedFiles,
    string CurrentFile,
    long TotalBytes,
    long ProcessedBytes);

public sealed record VaultImportResult(
    int Imported,
    int Skipped,
    IReadOnlyList<string> Errors,
    bool Cancelled);

public sealed record VaultExportResult(int Exported, bool Cancelled);

public sealed class EncryptedVaultService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _root;
    private readonly int _iterations;

    public EncryptedVaultService(string? root = null, int iterations = 600_000)
    {
        _root = root ?? Path.Combine(ConfigPaths.AppDataDir, "vaults");
        _iterations = Math.Max(1000, iterations);
    }

    public string Root => _root;

    public IReadOnlyList<VaultDescriptor> ListVaults()
    {
        var list = new List<VaultDescriptor>();
        try
        {
            if (!Directory.Exists(_root))
                return list;
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                try
                {
                    var metadataPath = Path.Combine(directory, "vault.json");
                    if (!File.Exists(metadataPath))
                        continue;
                    var metadata = JsonSerializer.Deserialize<VaultMetadata>(
                        File.ReadAllText(metadataPath), JsonOptions);
                    if (metadata is null)
                        continue;
                    list.Add(new VaultDescriptor(Path.GetFileName(directory), metadata.Name,
                        directory, metadata.CreatedAt));
                }
                catch
                {
                    // 忽略损坏的 vault 目录
                }
            }
        }
        catch
        {
            // 忽略根目录读取失败
        }

        return list.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<VaultDescriptor> CreateVaultAsync(string name, string password)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("文件柜名称不能为空。", nameof(name));
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ArgumentException("主密码至少 8 位。", nameof(password));

        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_root, id);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "blobs"));

        var salt = RandomNumberGenerator.GetBytes(16);
        var masterKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var kek = DeriveKey(password, salt, _iterations);
            var wrappedKey = VaultCrypto.EncryptBytes(masterKey, kek,
                Encoding.UTF8.GetBytes("vault-key"));
            CryptographicOperations.ZeroMemory(kek);

            var metadata = new VaultMetadata
            {
                Name = name.Trim(),
                Iterations = _iterations,
                Salt = salt,
                WrappedKey = wrappedKey,
                CreatedAt = DateTimeOffset.Now
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "vault.json"),
                JsonSerializer.Serialize(metadata, JsonOptions));

            var session = new VaultSession(directory, metadata, masterKey,
                new VaultIndex(), ownsKey: true);
            await session.SaveIndexAsync();
            session.Lock();

            return new VaultDescriptor(id, metadata.Name, directory, metadata.CreatedAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public async Task<VaultDescriptor> RenameVaultAsync(string vaultId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("文件柜名称不能为空。", nameof(newName));

        var directory = Path.Combine(_root, vaultId);
        var metadataPath = Path.Combine(directory, "vault.json");
        if (!File.Exists(metadataPath))
            throw new InvalidOperationException("文件柜不存在。");

        var metadata = JsonSerializer.Deserialize<VaultMetadata>(
            await File.ReadAllTextAsync(metadataPath), JsonOptions)
            ?? throw new InvalidDataException("文件柜元数据损坏。");
        metadata.Name = newName.Trim();

        var temp = metadataPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(metadata, JsonOptions));
        File.Move(temp, metadataPath, overwrite: true);
        return new VaultDescriptor(vaultId, metadata.Name, directory, metadata.CreatedAt);
    }

    public async Task ChangePasswordAsync(string vaultId, string oldPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            throw new ArgumentException("新主密码至少 8 位。", nameof(newPassword));

        var directory = Path.Combine(_root, vaultId);
        var metadataPath = Path.Combine(directory, "vault.json");
        if (!File.Exists(metadataPath))
            throw new InvalidOperationException("文件柜不存在。");

        var metadata = JsonSerializer.Deserialize<VaultMetadata>(
            await File.ReadAllTextAsync(metadataPath), JsonOptions)
            ?? throw new InvalidDataException("文件柜元数据损坏。");

        var oldKek = DeriveKey(oldPassword, metadata.Salt, metadata.Iterations);
        byte[] masterKey;
        try
        {
            masterKey = VaultCrypto.DecryptBytes(metadata.WrappedKey, oldKek,
                Encoding.UTF8.GetBytes("vault-key"));
        }
        catch (CryptographicException)
        {
            throw new VaultUnlockException("旧密码错误。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldKek);
        }

        try
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var kek = DeriveKey(newPassword, salt, _iterations);
            try
            {
                var wrapped = VaultCrypto.EncryptBytes(masterKey, kek,
                    Encoding.UTF8.GetBytes("vault-key"));
                metadata.Salt = salt;
                metadata.Iterations = _iterations;
                metadata.WrappedKey = wrapped;

                var temp = metadataPath + ".tmp";
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(metadata, JsonOptions));
                File.Move(temp, metadataPath, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public async Task DeleteVaultAsync(string vaultId, string password)
    {
        var directory = Path.Combine(_root, vaultId);
        if (!File.Exists(Path.Combine(directory, "vault.json")))
            throw new InvalidOperationException("文件柜不存在。");

        using (var session = await UnlockAsync(vaultId, password))
        {
            session.Lock();
        }

        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    public async Task ExportPackageAsync(string vaultId, string packagePath,
        IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
    {
        var directory = Path.Combine(_root, vaultId);
        if (!File.Exists(Path.Combine(directory, "vault.json")))
            throw new InvalidOperationException("文件柜不存在。");

        var temp = packagePath + ".tmp";
        try
        {
            if (File.Exists(temp))
                File.Delete(temp);

            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .ToArray();
            var totalBytes = files.Sum(f => f.Length);

            await Task.Run(() =>
            {
                using var output = File.Create(temp);
                using var archive = new System.IO.Compression.ZipArchive(
                    output, System.IO.Compression.ZipArchiveMode.Create);
                var processed = 0L;
                for (var i = 0; i < files.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = files[i];
                    var relative = Path.GetRelativePath(directory, file.FullName);
                    progress?.Report(new VaultProgress(files.Length, i, relative, totalBytes, processed));
                    var entry = archive.CreateEntry(relative,
                        System.IO.Compression.CompressionLevel.NoCompression);
                    using var entryStream = entry.Open();
                    using var input = file.OpenRead();
                    var fileProcessed = 0L;
                    processed += CopyWithProgress(input, entryStream, ct, bytes =>
                    {
                        fileProcessed += bytes;
                        progress?.Report(new VaultProgress(files.Length, i, relative,
                            totalBytes, processed + fileProcessed));
                    });
                }
                progress?.Report(new VaultProgress(files.Length, files.Length, "完成", totalBytes, processed));
            }, ct);

            File.Move(temp, packagePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
        }
    }

    public async Task<VaultDescriptor> ImportPackageAsync(string packagePath,
        IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var temp = Path.Combine(_root, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            var tempRoot = Path.GetFullPath(temp).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var archive = System.IO.Compression.ZipFile.OpenRead(packagePath))
            {
                var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToArray();
                var totalBytes = entries.Sum(e => e.Length);

                await Task.Run(() =>
                {
                    var processed = 0L;
                    for (var i = 0; i < entries.Length; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = entries[i];
                        var target = Path.GetFullPath(Path.Combine(temp, entry.FullName));
                        if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("加密包包含非法路径。");

                        var directory = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(directory))
                            Directory.CreateDirectory(directory);

                        progress?.Report(new VaultProgress(entries.Length, i, entry.FullName,
                            totalBytes, processed));
                        using var input = entry.Open();
                        using var output = File.Create(target);
                        var fileProcessed = 0L;
                        processed += CopyWithProgress(input, output, ct, bytes =>
                        {
                            fileProcessed += bytes;
                            progress?.Report(new VaultProgress(entries.Length, i, entry.FullName,
                                totalBytes, processed + fileProcessed));
                        });
                    }
                }, ct);
            }

            var metadataPath = Path.Combine(temp, "vault.json");
            var metadata = JsonSerializer.Deserialize<VaultMetadata>(
                await File.ReadAllTextAsync(metadataPath), JsonOptions)
                ?? throw new InvalidDataException("加密包元数据损坏。");

            var id = Guid.NewGuid().ToString("N");
            var destination = Path.Combine(_root, id);
            Directory.Move(temp, destination);
            temp = string.Empty;
            return new VaultDescriptor(id, metadata.Name, destination, metadata.CreatedAt);
        }
        finally
        {
            if (!string.IsNullOrEmpty(temp))
            {
                try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    private static long CopyWithProgress(Stream input, Stream output, CancellationToken ct, Action<long> onBytes)
    {
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            total += read;
            onBytes(read);
        }
        return total;
    }

    public async Task<VaultSession> UnlockAsync(string vaultId, string password)
    {
        var directory = Path.Combine(_root, vaultId);
        var metadataPath = Path.Combine(directory, "vault.json");
        if (!File.Exists(metadataPath))
            throw new VaultUnlockException("文件柜不存在。");

        var metadata = JsonSerializer.Deserialize<VaultMetadata>(
            await File.ReadAllTextAsync(metadataPath), JsonOptions)
            ?? throw new VaultUnlockException("文件柜元数据损坏。");

        var kek = DeriveKey(password, metadata.Salt, metadata.Iterations);
        byte[] masterKey;
        try
        {
            masterKey = VaultCrypto.DecryptBytes(metadata.WrappedKey, kek,
                Encoding.UTF8.GetBytes("vault-key"));
        }
        catch (CryptographicException)
        {
            throw new VaultUnlockException("密码错误。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        var index = new VaultIndex();
        var indexPath = Path.Combine(directory, "index.bin");
        if (File.Exists(indexPath))
        {
            try
            {
                var encrypted = await File.ReadAllBytesAsync(indexPath);
                var plain = VaultCrypto.DecryptBytes(encrypted, masterKey,
                    Encoding.UTF8.GetBytes("index"));
                index = JsonSerializer.Deserialize<VaultIndex>(plain, JsonOptions) ?? new VaultIndex();
            }
            catch (Exception ex)
            {
                CryptographicOperations.ZeroMemory(masterKey);
                throw new VaultUnlockException("文件柜索引损坏：" + ex.Message);
            }
        }

        return new VaultSession(directory, metadata, masterKey, index, ownsKey: true);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
}

public sealed class VaultSession : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly VaultMetadata _metadata;
    private readonly VaultIndex _index;
    private readonly byte[] _masterKey;
    private bool _locked;

    internal VaultSession(string directory, VaultMetadata metadata, byte[] masterKey,
        VaultIndex index, bool ownsKey)
    {
        _directory = directory;
        _metadata = metadata;
        _masterKey = masterKey;
        _index = index;
    }

    public string Name => _metadata.Name;
    public IReadOnlyList<VaultEntry> Entries => _index.Entries;
    public bool IsLocked => _locked;

    public IReadOnlyList<VaultEntry> Search(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return _index.Entries.OrderByDescending(e => e.ModifiedAt).ToArray();
        var text = keyword.Trim();
        return _index.Entries
            .Where(e => e.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                        e.RelativePath.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(e => e.ModifiedAt)
            .ToArray();
    }

    public async Task<byte[]> ReadFileBytesAsync(VaultEntry entry, long maxBytes,
        CancellationToken ct = default)
    {
        EnsureUnlocked();
        if (entry.Size > maxBytes)
            throw new InvalidOperationException("文件过大，无法在内存中打开。");
        var blob = Path.Combine(_directory, "blobs", entry.Id.ToString("N") + ".bin");
        if (!File.Exists(blob))
            throw new FileNotFoundException("文件柜数据不存在。", blob);

        var dataKey = VaultCrypto.UnwrapKey(entry.WrappedDataKey, _masterKey, entry.Id);
        try
        {
            return await Task.Run(() => VaultCrypto.DecryptFileToBytes(blob, dataKey, maxBytes, ct), ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task SaveFileBytesAsync(VaultEntry entry, byte[] content,
        DateTimeOffset expectedModified, CancellationToken ct = default)
    {
        EnsureUnlocked();
        if (entry.ModifiedAt != expectedModified)
            throw new InvalidOperationException("文件已被其他窗口修改，请重新打开后再保存。");

        var blob = Path.Combine(_directory, "blobs", entry.Id.ToString("N") + ".bin");
        var temp = blob + ".tmp";
        var dataKey = VaultCrypto.UnwrapKey(entry.WrappedDataKey, _masterKey, entry.Id);
        try
        {
            await Task.Run(() => VaultCrypto.EncryptBytesToFile(content, temp, dataKey, ct: ct), ct);
            File.Move(temp, blob, overwrite: true);
            entry.Size = content.Length;
            entry.ModifiedAt = DateTimeOffset.Now;
            await SaveIndexAsync();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
        }
    }

    public async Task<VaultImportResult> ImportPathsAsync(IEnumerable<string> paths,
        IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureUnlocked();
        var candidates = new List<(string Source, string Relative, long Size)>();
        var existing = _index.Entries.Select(e => e.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                var relative = Path.GetFileName(path);
                if (existing.Add(relative))
                    candidates.Add((path, relative, new FileInfo(path).Length));
                else
                    skipped++;
            }
            else if (Directory.Exists(path))
            {
                var parent = Path.GetDirectoryName(path) ?? path;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(parent, file);
                    if (existing.Add(relative))
                        candidates.Add((file, relative, new FileInfo(file).Length));
                    else
                        skipped++;
                }
            }
        }

        var imported = 0;
        var errors = new List<string>();
        var totalBytes = candidates.Sum(c => c.Size);
        var processedBytes = 0L;
        var cancelled = false;

        try
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = candidates[i];
                progress?.Report(new VaultProgress(candidates.Count, i, candidate.Relative,
                    totalBytes, processedBytes));

                var ok = await ImportOneAsync(candidate.Source, candidate.Relative, errors, ct,
                    bytes =>
                    {
                        processedBytes += bytes;
                        progress?.Report(new VaultProgress(candidates.Count, i, candidate.Relative,
                            totalBytes, processedBytes));
                    });
                if (ok)
                    imported++;
                else
                    skipped++;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            await SaveIndexAsync();
        }

        progress?.Report(new VaultProgress(candidates.Count, candidates.Count,
            cancelled ? "已取消" : "完成", totalBytes, processedBytes));
        return new VaultImportResult(imported, skipped, errors, cancelled);
    }

    private async Task<bool> ImportOneAsync(string sourcePath, string relativePath, List<string> errors,
        CancellationToken ct, Action<long> onBytes)
    {
        var id = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var blobPath = Path.Combine(_directory, "blobs", id.ToString("N") + ".bin");
        try
        {
            var wrapped = VaultCrypto.WrapKey(dataKey, _masterKey, id);
            await Task.Run(() => VaultCrypto.EncryptFile(sourcePath, blobPath, dataKey, onBytes, ct), ct);

            var info = new FileInfo(sourcePath);
            _index.Entries.Add(new VaultEntry
            {
                Id = id,
                Name = Path.GetFileName(relativePath),
                RelativePath = relativePath,
                Size = info.Length,
                CreatedAt = DateTimeOffset.Now,
                ModifiedAt = info.LastWriteTimeUtc,
                WrappedDataKey = wrapped
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(blobPath); } catch { /* ignore */ }
            throw;
        }
        catch (Exception ex)
        {
            try { File.Delete(blobPath); } catch { /* ignore */ }
            errors.Add($"{relativePath}: {ex.Message}");
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task<VaultExportResult> ExportAsync(IEnumerable<VaultEntry> entries, string destination,
        IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureUnlocked();
        Directory.CreateDirectory(destination);
        var list = entries.ToArray();
        var totalBytes = list.Sum(e => e.Size);
        var processedBytes = 0L;
        var count = 0;
        var cancelled = false;

        try
        {
            for (var i = 0; i < list.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = list[i];
                var relative = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(destination, relative));
                var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var blob = Path.Combine(_directory, "blobs", entry.Id.ToString("N") + ".bin");
                if (!File.Exists(blob))
                    continue;

                var dataKey = VaultCrypto.UnwrapKey(entry.WrappedDataKey, _masterKey, entry.Id);
                try
                {
                    progress?.Report(new VaultProgress(list.Length, i, entry.RelativePath,
                        totalBytes, processedBytes));
                    await Task.Run(() => VaultCrypto.DecryptFile(blob, target, dataKey, bytes =>
                    {
                        processedBytes += bytes;
                        progress?.Report(new VaultProgress(list.Length, i, entry.RelativePath,
                            totalBytes, processedBytes));
                    }, ct), ct);
                    count++;
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(target); } catch { /* ignore */ }
                    throw;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(dataKey);
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        progress?.Report(new VaultProgress(list.Length, list.Length,
            cancelled ? "已取消" : "完成", totalBytes, processedBytes));
        return new VaultExportResult(count, cancelled);
    }

    public async Task DeleteAsync(IEnumerable<VaultEntry> entries)
    {
        EnsureUnlocked();
        foreach (var entry in entries.ToArray())
        {
            _index.Entries.Remove(entry);
            var blob = Path.Combine(_directory, "blobs", entry.Id.ToString("N") + ".bin");
            try { File.Delete(blob); } catch { /* ignore */ }
        }
        await SaveIndexAsync();
    }

    public async Task RenameAsync(VaultEntry entry, string newName)
    {
        EnsureUnlocked();
        if (string.IsNullOrWhiteSpace(newName))
            return;
        entry.Name = newName.Trim();
        await SaveIndexAsync();
    }

    public async Task SaveIndexAsync()
    {
        EnsureUnlocked();
        var json = JsonSerializer.SerializeToUtf8Bytes(_index, JsonOptions);
        var encrypted = VaultCrypto.EncryptBytes(json, _masterKey, Encoding.UTF8.GetBytes("index"));
        var path = Path.Combine(_directory, "index.bin");
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, encrypted);
        File.Move(temp, path, overwrite: true);
    }

    public void Lock()
    {
        if (_locked)
            return;
        _locked = true;
        CryptographicOperations.ZeroMemory(_masterKey);
        _index.Entries.Clear();
    }

    public void Dispose() => Lock();

    private void EnsureUnlocked()
    {
        if (_locked)
            throw new InvalidOperationException("文件柜已锁定。");
    }
}
