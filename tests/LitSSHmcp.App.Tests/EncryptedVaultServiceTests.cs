using System.IO;
using System.Text;
using LitSSHmcp.App.Services.Toolbox;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class EncryptedVaultServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-vault-" + Guid.NewGuid().ToString("N"));

    public EncryptedVaultServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Create_unlock_import_export_roundtrip_keeps_plaintext_out_of_disk()
    {
        var service = new EncryptedVaultService(_root, iterations: 1000);
        var vault = await service.CreateVaultAsync("测试文件柜", "password123");

        await Assert.ThrowsAsync<VaultUnlockException>(() =>
            service.UnlockAsync(vault.Id, "wrong-password"));

        using var session = await service.UnlockAsync(vault.Id, "password123");
        var source = Path.Combine(_root, "secret.txt");
        const string content = "hello-vault-secret";
        await File.WriteAllTextAsync(source, content);

        var import = await session.ImportPathsAsync(new[] { source });
        Assert.Equal(1, import.Imported);
        Assert.Single(session.Entries);

        var blob = Directory.GetFiles(Path.Combine(vault.DirectoryPath, "blobs")).Single();
        var blobText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(blob));
        Assert.DoesNotContain(content, blobText);

        var indexBytes = await File.ReadAllBytesAsync(Path.Combine(vault.DirectoryPath, "index.bin"));
        Assert.DoesNotContain("secret.txt", Encoding.UTF8.GetString(indexBytes));

        var destination = Path.Combine(_root, "export");
        var exported = await session.ExportAsync(session.Entries, destination);
        Assert.Equal(1, exported.Exported);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(destination, "secret.txt")));

        session.Lock();
        using var reopened = await service.UnlockAsync(vault.Id, "password123");
        Assert.Single(reopened.Entries);
    }

    [Fact]
    public async Task Import_folder_preserves_relative_paths()
    {
        var service = new EncryptedVaultService(_root, iterations: 1000);
        var vault = await service.CreateVaultAsync("folder", "password123");
        using var session = await service.UnlockAsync(vault.Id, "password123");

        var folder = Path.Combine(_root, "资料");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(folder, "sub", "b.txt"), "b");

        var import = await session.ImportPathsAsync(new[] { folder });
        Assert.Equal(2, import.Imported);
        Assert.Contains(session.Entries, e => e.RelativePath == Path.Combine("资料", "a.txt"));
        Assert.Contains(session.Entries, e => e.RelativePath == Path.Combine("资料", "sub", "b.txt"));

        var destination = Path.Combine(_root, "out");
        var exported = await session.ExportAsync(session.Entries, destination);
        Assert.Equal(2, exported.Exported);
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(destination, "资料", "sub", "b.txt")));
    }

    [Fact]
    public async Task Read_and_save_file_bytes_detects_concurrent_changes()
    {
        var service = new EncryptedVaultService(_root, iterations: 1000);
        var vault = await service.CreateVaultAsync("内存编辑", "password123");
        using var session = await service.UnlockAsync(vault.Id, "password123");
        var source = Path.Combine(_root, "edit.txt");
        await File.WriteAllTextAsync(source, "before");
        await session.ImportPathsAsync(new[] { source });
        var entry = session.Entries.Single();
        var expected = entry.ModifiedAt;

        var before = await session.ReadFileBytesAsync(entry, 1024 * 1024);
        Assert.Equal("before", Encoding.UTF8.GetString(before));

        var after = Encoding.UTF8.GetBytes("after-edit");
        await session.SaveFileBytesAsync(entry, after, expected);
        Assert.Equal(after.Length, entry.Size);
        var read = await session.ReadFileBytesAsync(entry, 1024 * 1024);
        Assert.Equal("after-edit", Encoding.UTF8.GetString(read));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SaveFileBytesAsync(entry, Encoding.UTF8.GetBytes("stale"), expected));
    }

    [Fact]
    public async Task Rename_vault_updates_metadata_and_keeps_unlock_working()
    {
        var service = new EncryptedVaultService(_root, iterations: 1000);
        var vault = await service.CreateVaultAsync("旧名称", "password123");
        var renamed = await service.RenameVaultAsync(vault.Id, "新名称");

        Assert.Equal("新名称", renamed.Name);
        Assert.Contains(service.ListVaults(), v => v.Id == vault.Id && v.Name == "新名称");
        using var session = await service.UnlockAsync(vault.Id, "password123");
        Assert.Equal("新名称", session.Name);
    }

    [Fact]
    public async Task Change_password_rewraps_master_key_and_keeps_entries()
    {
        var service = new EncryptedVaultService(_root, iterations: 1000);
        var vault = await service.CreateVaultAsync("密码轮换", "password123");
        using (var session = await service.UnlockAsync(vault.Id, "password123"))
        {
            var source = Path.Combine(_root, "rotate.txt");
            await File.WriteAllTextAsync(source, "rotate");
            await session.ImportPathsAsync(new[] { source });
        }

        await Assert.ThrowsAsync<VaultUnlockException>(() =>
            service.ChangePasswordAsync(vault.Id, "wrong-password", "newpassword123"));
        await service.ChangePasswordAsync(vault.Id, "password123", "newpassword123");

        await Assert.ThrowsAsync<VaultUnlockException>(() =>
            service.UnlockAsync(vault.Id, "password123"));
        using var reopened = await service.UnlockAsync(vault.Id, "newpassword123");
        Assert.Single(reopened.Entries);
    }

    [Fact]
    public async Task Delete_vault_requires_password_and_removes_directory()
    {
        var service = new EncryptedVaultService(_root, iterations: 1000);
        var vault = await service.CreateVaultAsync("待删除", "password123");

        await Assert.ThrowsAsync<VaultUnlockException>(() =>
            service.DeleteVaultAsync(vault.Id, "wrong-password"));
        Assert.Contains(service.ListVaults(), v => v.Id == vault.Id);

        await service.DeleteVaultAsync(vault.Id, "password123");
        Assert.DoesNotContain(service.ListVaults(), v => v.Id == vault.Id);
        Assert.False(Directory.Exists(vault.DirectoryPath));
    }

    [Fact]
    public async Task Export_and_import_encrypted_package_roundtrip()
    {
        var service = new EncryptedVaultService(_root, iterations: 1000);
        var vault = await service.CreateVaultAsync("便携包", "password123");
        using (var session = await service.UnlockAsync(vault.Id, "password123"))
        {
            var source = Path.Combine(_root, "package.txt");
            await File.WriteAllTextAsync(source, "packed");
            await session.ImportPathsAsync(new[] { source });
        }

        var package = Path.Combine(_root, "vault.lsvault");
        await service.ExportPackageAsync(vault.Id, package);
        Assert.True(File.Exists(package));

        var imported = await service.ImportPackageAsync(package);
        Assert.NotEqual(vault.Id, imported.Id);
        using var importedSession = await service.UnlockAsync(imported.Id, "password123");
        Assert.Single(importedSession.Entries);
        Assert.Equal("package.txt", importedSession.Entries[0].Name);
    }

    [Fact]
    public async Task Import_reports_progress_and_honors_cancellation()
    {
        var service = new EncryptedVaultService(_root, iterations: 1000);
        var vault = await service.CreateVaultAsync("progress", "password123");
        using var session = await service.UnlockAsync(vault.Id, "password123");
        var source = Path.Combine(_root, "progress.txt");
        await File.WriteAllTextAsync(source, new string('x', 2 * 1024 * 1024));

        var reports = new List<VaultProgress>();
        var progress = new InlineProgress<VaultProgress>(reports.Add);
        var imported = await session.ImportPathsAsync(new[] { source }, progress);
        Assert.Equal(1, imported.Imported);
        Assert.NotEmpty(reports);
        Assert.Contains(reports, r => r.TotalFiles == 1 && r.CompletedFiles >= 0);
        Assert.Contains(reports, r => r.ProcessedBytes > 0);

        var second = Path.Combine(_root, "cancel.txt");
        await File.WriteAllTextAsync(second, "cancel");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await session.ImportPathsAsync(new[] { second }, null, cts.Token);
        Assert.True(cancelled.Cancelled);
        Assert.Equal(0, cancelled.Imported);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
