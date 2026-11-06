using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.Toolbox;

/// <summary>单密码保管箱：独立主密码、加密索引、加密附件。</summary>
public sealed class PasswordVaultService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _root;
    private readonly int _iterations;
    private int _failedUnlockAttempts;
    private DateTimeOffset _unlockBlockedUntil = DateTimeOffset.MinValue;

    public PasswordVaultService(string? root = null, int iterations = 600_000)
    {
        _root = root ?? Path.Combine(ConfigPaths.AppDataDir, "password-vault");
        _iterations = Math.Max(1000, iterations);
    }

    public string Root => _root;
    public bool Exists => File.Exists(Path.Combine(_root, "vault.json"));

    public async Task<PasswordVaultSession> CreateAsync(string name, string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ArgumentException("主密码至少 8 位。", nameof(password));
        if (Exists)
            throw new InvalidOperationException("密码保管箱已存在。");

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "attachments"));

        var salt = RandomNumberGenerator.GetBytes(16);
        var masterKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var kek = DeriveKey(password, salt, _iterations);
            var wrappedKey = VaultCrypto.EncryptBytes(masterKey, kek,
                Encoding.UTF8.GetBytes("password-vault-key"));
            CryptographicOperations.ZeroMemory(kek);

            var metadata = new VaultMetadata
            {
                Name = string.IsNullOrWhiteSpace(name) ? "密码保管箱" : name.Trim(),
                Iterations = _iterations,
                Salt = salt,
                WrappedKey = wrappedKey,
                CreatedAt = DateTimeOffset.Now
            };
            await File.WriteAllTextAsync(Path.Combine(_root, "vault.json"),
                JsonSerializer.Serialize(metadata, JsonOptions));

            var session = new PasswordVaultSession(_root, metadata, masterKey,
                new PasswordVaultIndex { Name = metadata.Name }, ownsKey: true);
            await session.SaveIndexAsync();
            return session;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw;
        }
    }

    public async Task<PasswordVaultSession> UnlockAsync(string password)
    {
        var wait = _unlockBlockedUntil - DateTimeOffset.Now;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait);
        _unlockBlockedUntil = DateTimeOffset.MinValue;

        var metadataPath = Path.Combine(_root, "vault.json");
        if (!File.Exists(metadataPath))
            throw new InvalidOperationException("密码保管箱尚未创建。");

        var metadata = JsonSerializer.Deserialize<VaultMetadata>(
            await File.ReadAllTextAsync(metadataPath), JsonOptions)
            ?? throw new InvalidDataException("密码保管箱元数据损坏。");

        var kek = DeriveKey(password, metadata.Salt, metadata.Iterations);
        byte[] masterKey;
        try
        {
            masterKey = VaultCrypto.DecryptBytes(metadata.WrappedKey, kek,
                Encoding.UTF8.GetBytes("password-vault-key"));
        }
        catch (CryptographicException)
        {
            _failedUnlockAttempts++;
            var delaySeconds = _failedUnlockAttempts < 3
                ? 0
                : Math.Min(30, 2 << Math.Min(4, _failedUnlockAttempts - 3));
            if (delaySeconds > 0)
                _unlockBlockedUntil = DateTimeOffset.Now.AddSeconds(delaySeconds);
            throw new VaultUnlockException(delaySeconds > 0
                ? $"主密码错误，请 {delaySeconds} 秒后重试。"
                : "主密码错误。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        _failedUnlockAttempts = 0;
        _unlockBlockedUntil = DateTimeOffset.MinValue;
        try
        {
            var indexPath = Path.Combine(_root, "index.bin");
            PasswordVaultIndex index;
            if (File.Exists(indexPath))
            {
                var encrypted = await File.ReadAllBytesAsync(indexPath);
                var json = VaultCrypto.DecryptBytes(encrypted, masterKey,
                    Encoding.UTF8.GetBytes("password-index"));
                index = JsonSerializer.Deserialize<PasswordVaultIndex>(json, JsonOptions)
                    ?? new PasswordVaultIndex();
            }
            else
            {
                index = new PasswordVaultIndex { Name = metadata.Name };
            }

            return new PasswordVaultSession(_root, metadata, masterKey, index, ownsKey: true);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw;
        }
    }

    public async Task ChangePasswordAsync(string oldPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            throw new ArgumentException("新主密码至少 8 位。", nameof(newPassword));

        var metadataPath = Path.Combine(_root, "vault.json");
        if (!File.Exists(metadataPath))
            throw new InvalidOperationException("密码保管箱尚未创建。");

        var metadata = JsonSerializer.Deserialize<VaultMetadata>(
            await File.ReadAllTextAsync(metadataPath), JsonOptions)
            ?? throw new InvalidDataException("密码保管箱元数据损坏。");

        var oldKek = DeriveKey(oldPassword, metadata.Salt, metadata.Iterations);
        byte[] masterKey;
        try
        {
            masterKey = VaultCrypto.DecryptBytes(metadata.WrappedKey, oldKek,
                Encoding.UTF8.GetBytes("password-vault-key"));
        }
        catch (CryptographicException)
        {
            throw new VaultUnlockException("旧主密码错误。");
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
                metadata.Salt = salt;
                metadata.Iterations = _iterations;
                metadata.WrappedKey = VaultCrypto.EncryptBytes(masterKey, kek,
                    Encoding.UTF8.GetBytes("password-vault-key"));
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

    public async Task ExportPackageAsync(string packagePath, CancellationToken ct = default)
    {
        if (!Exists)
            throw new InvalidOperationException("密码保管箱尚未创建。");

        var temp = Path.Combine(Path.GetTempPath(),
            "litssh-password-vault-" + Guid.NewGuid().ToString("N") + ".lspvault.tmp");
        try
        {
            await Task.Run(() => ZipFile.CreateFromDirectory(_root, temp,
                CompressionLevel.Optimal, includeBaseDirectory: false), ct);
            await Task.Run(() => File.Move(temp, packagePath, overwrite: true), ct);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
        }
    }

    public async Task<string?> RestorePackageAsync(string packagePath, string password,
        CancellationToken ct = default)
    {
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("备份包不存在。", packagePath);
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("请输入备份包主密码。", nameof(password));

        var tempRoot = Path.Combine(Path.GetTempPath(),
            "litssh-password-vault-restore-" + Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(tempRoot, "vault");
        try
        {
            Directory.CreateDirectory(extractDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(packagePath, extractDir), ct);
            await VerifyPackageAsync(extractDir, password, ct);

            var parent = Path.GetDirectoryName(_root.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            string? backupPath = null;
            if (Directory.Exists(_root))
            {
                backupPath = _root + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                Directory.Move(_root, backupPath);
            }

            try
            {
                Directory.Move(extractDir, _root);
            }
            catch
            {
                try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
                if (backupPath is not null && Directory.Exists(backupPath) && !Directory.Exists(_root))
                    Directory.Move(backupPath, _root);
                throw;
            }

            PruneBackups();
            return backupPath;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { /* ignore */ }
        }
    }

    private void PruneBackups()
    {
        try
        {
            var parent = Path.GetDirectoryName(_root.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                return;
            var name = Path.GetFileName(_root.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var backups = Directory.GetDirectories(parent, name + ".backup-*")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Skip(5)
                .ToArray();
            foreach (var backup in backups)
            {
                try { Directory.Delete(backup, recursive: true); } catch { /* ignore */ }
            }
        }
        catch
        {
            // 备份轮换失败不影响恢复结果
        }
    }

    private async Task VerifyPackageAsync(string directory, string password, CancellationToken ct)
    {
        var metadataPath = Path.Combine(directory, "vault.json");
        var indexPath = Path.Combine(directory, "index.bin");
        if (!File.Exists(metadataPath) || !File.Exists(indexPath))
            throw new InvalidDataException("备份包不是有效的密码保管箱备份。");

        var metadata = JsonSerializer.Deserialize<VaultMetadata>(
            await File.ReadAllTextAsync(metadataPath, ct), JsonOptions)
            ?? throw new InvalidDataException("备份包元数据损坏。");

        var kek = DeriveKey(password, metadata.Salt, metadata.Iterations);
        byte[] masterKey;
        try
        {
            masterKey = VaultCrypto.DecryptBytes(metadata.WrappedKey, kek,
                Encoding.UTF8.GetBytes("password-vault-key"));
        }
        catch (CryptographicException)
        {
            throw new VaultUnlockException("备份包主密码错误。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(indexPath, ct);
            var json = VaultCrypto.DecryptBytes(encrypted, masterKey,
                Encoding.UTF8.GetBytes("password-index"));
            _ = JsonSerializer.Deserialize<PasswordVaultIndex>(json, JsonOptions)
                ?? throw new InvalidDataException("备份包索引损坏。");
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("备份包索引无法解密。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
}

public sealed class PasswordVaultSession : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _root;
    private readonly VaultMetadata _metadata;
    private readonly PasswordVaultIndex _index;
    private readonly byte[] _masterKey;
    private bool _locked;

    internal PasswordVaultSession(string root, VaultMetadata metadata, byte[] masterKey,
        PasswordVaultIndex index, bool ownsKey)
    {
        _root = root;
        _metadata = metadata;
        _masterKey = masterKey;
        _index = index;
    }

    public string Name => _metadata.Name;
    public IReadOnlyList<PasswordItem> Items => _index.Items;
    public bool IsLocked => _locked;

    public async Task SaveItemAsync(PasswordItem item)
    {
        EnsureUnlocked();
        item.UpdatedAt = DateTimeOffset.Now;
        item.PasswordHistory ??= new List<PasswordHistoryEntry>();
        var existing = _index.Items.FirstOrDefault(i => i.Id == item.Id);
        if (existing is null)
            _index.Items.Add(item);
        else
        {
            if (!string.IsNullOrEmpty(existing.Password) &&
                !string.Equals(existing.Password, item.Password, StringComparison.Ordinal))
            {
                item.PasswordHistory.Insert(0, new PasswordHistoryEntry
                {
                    Password = existing.Password,
                    ChangedAt = DateTimeOffset.Now
                });
                if (item.PasswordHistory.Count > 10)
                    item.PasswordHistory.RemoveRange(10, item.PasswordHistory.Count - 10);
            }
            var position = _index.Items.IndexOf(existing);
            _index.Items[position] = item;
        }
        await SaveIndexAsync();
    }

    public async Task DeleteItemAsync(PasswordItem item)
    {
        EnsureUnlocked();
        _index.Items.RemoveAll(i => i.Id == item.Id);
        foreach (var attachment in item.Attachments)
            TryDeleteAttachmentFile(attachment.Id);
        await SaveIndexAsync();
    }

    public async Task TouchItemAsync(PasswordItem item)
    {
        EnsureUnlocked();
        item.LastUsedAt = DateTimeOffset.Now;
        await SaveIndexAsync();
    }

    public async Task<PasswordAttachment> AddAttachmentAsync(PasswordItem item, string sourcePath)
    {
        EnsureUnlocked();
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("附件不存在。", sourcePath);

        var id = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var target = AttachmentPath(id);
        var temp = target + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, "attachments"));
            var wrapped = VaultCrypto.WrapKey(dataKey, _masterKey, id);
            await Task.Run(() => VaultCrypto.EncryptFile(sourcePath, temp, dataKey));
            File.Move(temp, target, overwrite: true);

            var attachment = new PasswordAttachment
            {
                Id = id,
                Name = Path.GetFileName(sourcePath),
                Size = new FileInfo(sourcePath).Length,
                CreatedAt = DateTimeOffset.Now,
                WrappedDataKey = wrapped
            };
            item.Attachments.Add(attachment);
            item.UpdatedAt = DateTimeOffset.Now;
            await SaveItemAsync(item);
            return attachment;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            try { if (File.Exists(target)) File.Delete(target); } catch { /* ignore */ }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task ExportAttachmentAsync(PasswordItem item, PasswordAttachment attachment,
        string targetPath, CancellationToken ct = default)
    {
        EnsureUnlocked();
        var source = AttachmentPath(attachment.Id);
        if (!File.Exists(source))
            throw new FileNotFoundException("附件数据不存在。", source);

        var dataKey = VaultCrypto.UnwrapKey(attachment.WrappedDataKey, _masterKey, attachment.Id);
        try
        {
            var temp = targetPath + ".tmp";
            await Task.Run(() => VaultCrypto.DecryptFile(source, temp, dataKey, ct: ct), ct);
            File.Move(temp, targetPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task<byte[]> ReadAttachmentBytesAsync(PasswordItem item, PasswordAttachment attachment,
        long maxBytes = 256L * 1024 * 1024, CancellationToken ct = default)
    {
        EnsureUnlocked();
        var source = AttachmentPath(attachment.Id);
        if (!File.Exists(source))
            throw new FileNotFoundException("附件数据不存在。", source);

        var dataKey = VaultCrypto.UnwrapKey(attachment.WrappedDataKey, _masterKey, attachment.Id);
        try
        {
            return await Task.Run(() =>
                VaultCrypto.DecryptFileToBytes(source, dataKey, maxBytes, ct), ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task DeleteAttachmentAsync(PasswordItem item, PasswordAttachment attachment)
    {
        EnsureUnlocked();
        item.Attachments.RemoveAll(a => a.Id == attachment.Id);
        TryDeleteAttachmentFile(attachment.Id);
        await SaveItemAsync(item);
    }

    public async Task SaveIndexAsync()
    {
        EnsureUnlocked();
        var json = JsonSerializer.SerializeToUtf8Bytes(_index, JsonOptions);
        var encrypted = VaultCrypto.EncryptBytes(json, _masterKey,
            Encoding.UTF8.GetBytes("password-index"));
        var path = Path.Combine(_root, "index.bin");
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
        _index.Items.Clear();
    }

    public void Dispose() => Lock();

    private string AttachmentPath(Guid id) =>
        Path.Combine(_root, "attachments", id.ToString("N") + ".bin");

    private void TryDeleteAttachmentFile(Guid id)
    {
        try { File.Delete(AttachmentPath(id)); } catch { /* ignore */ }
    }

    private void EnsureUnlocked()
    {
        if (_locked)
            throw new InvalidOperationException("密码保管箱已锁定。");
    }
}
