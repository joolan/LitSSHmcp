using System.IO;
using System.Threading;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views.Tools;
using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ToolboxEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "litssh-toolbox-env-" + Guid.NewGuid().ToString("N"));

    public ToolboxEnvironmentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Environment_values_are_encrypted_at_rest_and_decrypted_on_load()
    {
        var path = Path.Combine(_root, "toolbox.json");
        var store = new ToolboxStore(path, new FakeSecretProtector());
        var document = new ToolboxDocument
        {
            Items = new List<ToolboxItem>
            {
                new()
                {
                    Name = "demo",
                    Type = ToolboxItemType.Application,
                    Target = "demo.exe",
                    EnvironmentVariables = new List<ToolboxEnvironmentVariable>
                    {
                        new() { Name = "API_TOKEN", Value = "super-secret" }
                    }
                }
            }
        };

        await store.SaveAsync(document);
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("enc:", json);
        Assert.DoesNotContain("super-secret", json);

        var loaded = await store.LoadAsync();
        var variable = Assert.Single(loaded.Items[0].EnvironmentVariables);
        Assert.Equal("API_TOKEN", variable.Name);
        Assert.Equal("super-secret", variable.Value);
    }

    [Fact]
    public void Clone_deep_copies_environment_variables()
    {
        var item = new ToolboxItem
        {
            Name = "demo",
            EnvironmentVariables = new List<ToolboxEnvironmentVariable>
            {
                new() { Name = "TOKEN", Value = "original" }
            }
        };

        var clone = item.Clone();
        clone.EnvironmentVariables[0].Value = "changed";

        Assert.Equal("original", item.EnvironmentVariables[0].Value);
    }

    [Fact]
    public async Task Application_launch_injects_environment_variable()
    {
        var script = Path.Combine(_root, "env.cmd");
        await File.WriteAllTextAsync(script,
            "@echo off\r\n> \"out.txt\" echo %LITSSH_ENV_TEST%\r\n");
        var item = new ToolboxItem
        {
            Name = "env test",
            Type = ToolboxItemType.Application,
            Target = script,
            WorkingDirectory = _root,
            EnvironmentVariables = new List<ToolboxEnvironmentVariable>
            {
                new() { Name = "LITSSH_ENV_TEST", Value = "hello-from-toolbox" }
            }
        };

        var launcher = new ToolboxLauncher(new FakeDialogService());
        var result = await launcher.LaunchAsync(item);
        Assert.True(result.Success, result.Message);

        var output = Path.Combine(_root, "out.txt");
        for (var i = 0; i < 100 && (!File.Exists(output) || new FileInfo(output).Length == 0); i++)
            await Task.Delay(50);

        Assert.True(File.Exists(output));
        Assert.Contains("hello-from-toolbox", await File.ReadAllTextAsync(output));
        Assert.Null(Environment.GetEnvironmentVariable("LITSSH_ENV_TEST"));
    }

    [Fact]
    public async Task Administrator_launch_with_environment_variables_is_rejected()
    {
        var script = Path.Combine(_root, "admin.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\n");
        var item = new ToolboxItem
        {
            Name = "admin env",
            Type = ToolboxItemType.Application,
            Target = script,
            RunAsAdmin = true,
            EnvironmentVariables = new List<ToolboxEnvironmentVariable>
            {
                new() { Name = "TOKEN", Value = "value" }
            }
        };

        var launcher = new ToolboxLauncher(new FakeDialogService());
        var result = await launcher.LaunchAsync(item);
        Assert.False(result.Success);
        Assert.Contains("管理员", result.Message);
    }

    [Fact]
    public void Editor_validation_rejects_invalid_and_duplicate_names()
    {
        var viewModel = new ToolboxItemEditViewModel(new ToolboxItem
        {
            Name = "demo",
            Type = ToolboxItemType.Application,
            Target = "demo.exe"
        });
        viewModel.AddEnvironmentVariable();
        viewModel.EnvironmentVariables[0].Name = "1BAD";
        viewModel.EnvironmentVariables[0].Value = "x";
        Assert.Contains("不合法", viewModel.Validate());

        viewModel.EnvironmentVariables[0].Name = "TOKEN";
        viewModel.AddEnvironmentVariable();
        viewModel.EnvironmentVariables[1].Name = "token";
        viewModel.EnvironmentVariables[1].Value = "y";
        Assert.Contains("重复", viewModel.Validate());

        viewModel.EnvironmentVariables.RemoveAt(1);
        viewModel.RunAsAdmin = true;
        Assert.Contains("管理员", viewModel.Validate());
    }

    [Fact]
    public void Environment_windows_parse_xaml()
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new EnvironmentVariablesWindow();
                _ = new EnvironmentVariableEditWindow();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(captured);
    }

    [Fact]
    public void Environment_rules_protect_critical_variables_and_path()
    {
        Assert.Contains("关键", EnvironmentVariableRules.ValidateChange(
            new EnvironmentVariableChange(EnvironmentVariableScope.User, "Path", null,
                Microsoft.Win32.RegistryValueKind.String, Delete: true)));
        Assert.Contains("空路径段", EnvironmentVariableRules.ValidateChange(
            new EnvironmentVariableChange(EnvironmentVariableScope.User, "Path",
                @"C:\Windows;;C:\Tools", Microsoft.Win32.RegistryValueKind.String, Delete: false)));
        Assert.Contains("不合法", EnvironmentVariableRules.ValidateChange(
            new EnvironmentVariableChange(EnvironmentVariableScope.User, "1BAD", "x",
                Microsoft.Win32.RegistryValueKind.String, Delete: false)));
        Assert.Null(EnvironmentVariableRules.ValidateChange(
            new EnvironmentVariableChange(EnvironmentVariableScope.User, "TOKEN", "x",
                Microsoft.Win32.RegistryValueKind.String, Delete: false)));
    }

    [Fact]
    public async Task User_variable_can_be_applied_read_and_deleted()
    {
        var name = "LITSSH_ENV_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var backupDir = Path.Combine(_root, "env-backups");
        var service = new EnvironmentVariableService(backupDir);
        try
        {
            var added = await service.ApplyAsync(EnvironmentVariableScope.User,
                new[]
                {
                    new EnvironmentVariableChange(EnvironmentVariableScope.User, name,
                        "hello", Microsoft.Win32.RegistryValueKind.String, Delete: false)
                });
            Assert.True(added.Success, added.Message);

            var entry = Assert.Single(service.Read(EnvironmentVariableScope.User),
                item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("hello", entry.Value);
            Assert.Equal("hello", Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            await service.ApplyAsync(EnvironmentVariableScope.User,
                new[]
                {
                    new EnvironmentVariableChange(EnvironmentVariableScope.User, name,
                        null, Microsoft.Win32.RegistryValueKind.String, Delete: true)
                });
            Environment.SetEnvironmentVariable(name, null,
                EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void System_scope_starts_read_only_until_unlocked()
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new EnvironmentVariablesWindow();
                var tabs = (System.Windows.Controls.TabControl)window.FindName("ScopeTabs");
                tabs.SelectedIndex = 1;
                var add = (System.Windows.FrameworkElement)window.FindName("AddButton");
                var banner = (System.Windows.FrameworkElement)window.FindName("SystemLockBanner");
                Assert.False(add.IsEnabled);
                Assert.Equal(System.Windows.Visibility.Visible, banner.Visibility);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(captured);
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        private const string Prefix = "enc:";

        public bool IsProtected(string? value) =>
            value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

        public string? Protect(string? value) =>
            string.IsNullOrEmpty(value) ? value : Prefix + Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(value));

        public string? Unprotect(string? value) =>
            IsProtected(value)
                ? System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(value![Prefix.Length..]))
                : value;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
