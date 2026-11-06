using System.IO;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class PasswordVaultViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-pv-vm-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Unlock_reloads_existing_items_and_totp()
    {
        var service = new PasswordVaultService(_root, iterations: 1000);
        using (var session = await service.CreateAsync("密码保管箱", "password123"))
        {
            await session.SaveItemAsync(new PasswordItem
            {
                Title = "GitHub",
                Username = "user@example.com",
                Totp = new PasswordTotpConfig { Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ" }
            });
        }

        await StaTest.RunAsync(async () =>
        {
            using var viewModel = new PasswordVaultViewModel(service);
            Assert.False(viewModel.HasVault == false);
            Assert.True(await viewModel.UnlockAsync("password123"));
            Assert.True(viewModel.IsUnlocked);
            Assert.Single(viewModel.Items);
            Assert.Equal("GitHub", viewModel.Items[0].Title);
            Assert.NotNull(viewModel.SelectedItem);
            Assert.Equal(6, viewModel.TotpCode.Replace(" ", string.Empty).Length);
        });
    }

    [Fact]
    public async Task Restoring_history_swaps_current_password_into_history()
    {
        var service = new PasswordVaultService(_root, iterations: 1000);
        using (var session = await service.CreateAsync("密码保管箱", "password123"))
        {
            await session.SaveItemAsync(new PasswordItem { Title = "Mail", Password = "old-password" });
            var updated = session.Items.Single().Clone();
            updated.Password = "new-password";
            await session.SaveItemAsync(updated);
        }

        await StaTest.RunAsync(async () =>
        {
            using var viewModel = new PasswordVaultViewModel(service);
            Assert.True(await viewModel.UnlockAsync("password123"));
            viewModel.SelectedItem = viewModel.Items.Single();
            var history = viewModel.SelectedItem.PasswordHistory.Single();
            await viewModel.RestoreHistoryPasswordAsync(history);

            Assert.Equal("old-password", viewModel.SelectedItem!.Model.Password);
            Assert.Contains(viewModel.SelectedItem.PasswordHistory, h => h.Password == "new-password");
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
