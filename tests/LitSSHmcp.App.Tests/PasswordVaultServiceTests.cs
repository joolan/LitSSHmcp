using System.IO;
using System.Text;
using LitSSHmcp.App.Services.Toolbox;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class PasswordVaultServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-password-vault-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Create_unlock_crud_and_attachment_roundtrip_keeps_plaintext_out_of_disk()
    {
        var service = new PasswordVaultService(_root, iterations: 1000);
        Assert.False(service.Exists);

        using var created = await service.CreateAsync("密码保管箱", "password123");
        Assert.True(service.Exists);
        Assert.True(File.Exists(Path.Combine(_root, "index.bin")));

        var item = new PasswordItem
        {
            Title = "GitHub",
            Type = PasswordItemType.Login,
            Username = "user@example.com",
            Password = "super-secret-password",
            Url = "https://github.com",
            Group = "工作",
            SecurityPhone = "13800000000",
            RecoveryEmail = "ops@example.com",
            Tags = new List<string> { "开发", "重要" },
            Totp = new PasswordTotpConfig
            {
                Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ",
                Issuer = "GitHub",
                Account = "user@example.com"
            }
        };
        await created.SaveItemAsync(item);
        Assert.Single(created.Items);

        var source = Path.Combine(_root, "recovery.txt");
        const string attachmentText = "recovery-code-123";
        await File.WriteAllTextAsync(source, attachmentText);
        var attachment = await created.AddAttachmentAsync(created.Items.Single(), source);
        Assert.Equal("recovery.txt", attachment.Name);

        var indexPath = Path.Combine(_root, "index.bin");
        var indexBytes = await File.ReadAllBytesAsync(indexPath);
        Assert.DoesNotContain("GitHub", Encoding.UTF8.GetString(indexBytes));
        Assert.DoesNotContain("super-secret-password", Encoding.UTF8.GetString(indexBytes));

        var attachmentPath = Path.Combine(_root, "attachments", attachment.Id.ToString("N") + ".bin");
        var attachmentBytes = await File.ReadAllBytesAsync(attachmentPath);
        Assert.DoesNotContain(attachmentText, Encoding.UTF8.GetString(attachmentBytes));

        created.Lock();
        await Assert.ThrowsAsync<VaultUnlockException>(() => service.UnlockAsync("wrong-password"));

        using var reopened = await service.UnlockAsync("password123");
        var reopenedItem = Assert.Single(reopened.Items);
        Assert.Equal("GitHub", reopenedItem.Title);
        Assert.Equal("super-secret-password", reopenedItem.Password);
        Assert.Equal("工作", reopenedItem.Group);
        Assert.Equal("13800000000", reopenedItem.SecurityPhone);
        Assert.Equal("ops@example.com", reopenedItem.RecoveryEmail);
        Assert.NotNull(reopenedItem.Totp);
        Assert.Single(reopenedItem.Attachments);

        var destination = Path.Combine(_root, "export.txt");
        await reopened.ExportAttachmentAsync(reopenedItem, reopenedItem.Attachments.Single(), destination);
        Assert.Equal(attachmentText, await File.ReadAllTextAsync(destination));

        await reopened.DeleteAttachmentAsync(reopenedItem, reopenedItem.Attachments.Single());
        Assert.Empty(reopened.Items.Single().Attachments);
        Assert.False(File.Exists(attachmentPath));

        await reopened.DeleteItemAsync(reopened.Items.Single());
        Assert.Empty(reopened.Items);
    }

    [Fact]
    public async Task Export_and_restore_package_roundtrip_replaces_vault_and_keeps_backup()
    {
        var package = Path.Combine(Path.GetTempPath(), "litssh-pv-" + Guid.NewGuid().ToString("N") + ".lspvault");
        var sourceRoot = Path.Combine(_root, "source");
        var targetRoot = Path.Combine(_root, "target");
        try
        {
            var source = new PasswordVaultService(sourceRoot, iterations: 1000);
            using (var session = await source.CreateAsync("密码保管箱", "password123"))
            {
                var item = new PasswordItem
                {
                    Title = "GitHub",
                    Username = "user@example.com",
                    Password = "secret-value",
                    Totp = new PasswordTotpConfig { Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ" }
                };
                await session.SaveItemAsync(item);
                var attachmentSource = Path.Combine(_root, "backup-attachment.txt");
                await File.WriteAllTextAsync(attachmentSource, "attachment-secret");
                await session.AddAttachmentAsync(item, attachmentSource);
            }

            await source.ExportPackageAsync(package);
            Assert.True(File.Exists(package));
            Assert.DoesNotContain("secret-value", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(package)));

            var target = new PasswordVaultService(targetRoot, iterations: 1000);
            using (var oldSession = await target.CreateAsync("密码保管箱", "password123"))
                await oldSession.SaveItemAsync(new PasswordItem { Title = "旧条目" });

            var backup = await target.RestorePackageAsync(package, "password123");
            Assert.NotNull(backup);
            Assert.True(Directory.Exists(backup));

            using var restored = await target.UnlockAsync("password123");
            var restoredItem = Assert.Single(restored.Items);
            Assert.Equal("GitHub", restoredItem.Title);
            Assert.Equal("secret-value", restoredItem.Password);
            Assert.Single(restoredItem.Attachments);

            var destination = Path.Combine(_root, "restored-attachment.txt");
            await restored.ExportAttachmentAsync(restoredItem, restoredItem.Attachments.Single(), destination);
            Assert.Equal("attachment-secret", await File.ReadAllTextAsync(destination));
        }
        finally
        {
            try { File.Delete(package); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Restore_with_wrong_password_does_not_replace_current_vault()
    {
        var package = Path.Combine(Path.GetTempPath(), "litssh-pv-" + Guid.NewGuid().ToString("N") + ".lspvault");
        var sourceRoot = Path.Combine(_root, "source");
        var targetRoot = Path.Combine(_root, "target");
        try
        {
            var source = new PasswordVaultService(sourceRoot, iterations: 1000);
            using (var session = await source.CreateAsync("密码保管箱", "password123"))
                await session.SaveItemAsync(new PasswordItem { Title = "来自备份" });
            await source.ExportPackageAsync(package);

            var target = new PasswordVaultService(targetRoot, iterations: 1000);
            using (var session = await target.CreateAsync("密码保管箱", "password123"))
                await session.SaveItemAsync(new PasswordItem { Title = "当前条目" });

            await Assert.ThrowsAsync<VaultUnlockException>(() =>
                target.RestorePackageAsync(package, "wrong-password"));

            using var current = await target.UnlockAsync("password123");
            Assert.Equal("当前条目", Assert.Single(current.Items).Title);
        }
        finally
        {
            try { File.Delete(package); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Password_changes_are_kept_in_history_without_duplicates()
    {
        var service = new PasswordVaultService(_root, iterations: 1000);
        using var session = await service.CreateAsync("密码保管箱", "password123");
        await session.SaveItemAsync(new PasswordItem { Title = "Mail", Password = "old-password" });

        var updated = session.Items.Single().Clone();
        updated.Password = "new-password";
        await session.SaveItemAsync(updated);
        Assert.Single(session.Items.Single().PasswordHistory);
        Assert.Equal("old-password", session.Items.Single().PasswordHistory[0].Password);

        var unchanged = session.Items.Single().Clone();
        await session.SaveItemAsync(unchanged);
        Assert.Single(session.Items.Single().PasswordHistory);
    }

    [Fact]
    public async Task Change_password_rewraps_key_and_keeps_items()
    {
        var service = new PasswordVaultService(_root, iterations: 1000);
        using (var session = await service.CreateAsync("密码保管箱", "password123"))
        {
            await session.SaveItemAsync(new PasswordItem { Title = "Mail", Username = "a@b.c" });
        }

        await Assert.ThrowsAsync<VaultUnlockException>(() => service.ChangePasswordAsync("wrong", "newpassword123"));
        await service.ChangePasswordAsync("password123", "newpassword123");

        await Assert.ThrowsAsync<VaultUnlockException>(() => service.UnlockAsync("password123"));
        using var reopened = await service.UnlockAsync("newpassword123");
        Assert.Equal("Mail", Assert.Single(reopened.Items).Title);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
