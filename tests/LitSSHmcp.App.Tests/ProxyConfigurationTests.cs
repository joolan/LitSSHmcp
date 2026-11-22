using System.IO;
using System.Threading;
using LitSSHmcp.App.Services.Backup;
using LitSSHmcp.App.Views.Tools;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ProxyConfigurationTests
{
    [Fact]
    public void Backup_catalog_includes_http_socks_proxy_config()
    {
        var descriptor = Assert.Single(BackupItemCatalog.Descriptors,
            item => item.Id == "proxy-config");
        Assert.Equal("http-socks-proxy.json", Path.GetFileName(
            descriptor.ResolvePath(new AppConfig(), @"C:\data")));

        var item = Assert.Single(
            BackupItemCatalog.Build(new AppConfig(), @"C:\data", new[] { "proxy-config" }));
        Assert.Equal(@"C:\data\http-socks-proxy.json", item.Path);
        Assert.Equal(BackupItemKind.File, item.Descriptor.Kind);
    }

    [Fact]
    public void Proxy_tool_window_parses_xaml()
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { _ = new ProxyToolWindow(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(captured);
    }

    [Fact]
    public void Firewall_commands_cover_http_and_socks5_ports()
    {
        var service = new LitSSHmcp.App.Services.ProxyFirewallService();
        var options = new LitSSHmcp.Core.Services.Proxy.ProxyOptions
        {
            HttpEnabled = true,
            HttpPort = 18888,
            Socks5Enabled = true,
            Socks5Port = 11080
        };
        var commands = service.BuildAddCommands(options);
        Assert.Contains("localport=18888", commands);
        Assert.Contains("localport=11080", commands);
        Assert.Contains("profile=private", commands);
    }

    [Fact]
    public void Bind_address_options_include_loopback_and_all_interfaces()
    {
        var options = LitSSHmcp.App.Services.ProxyNetworkInterfaces
            .GetBindAddressOptions("127.0.0.1");
        Assert.Contains(options, item => item.Value == "127.0.0.1");
        Assert.Contains(options, item => item.Value == "0.0.0.0");
    }

    [Fact]
    public async Task Proxy_settings_encrypt_upstream_password()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "litssh-proxy-secret-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "http-socks-proxy.json");
            using var service = new LitSSHmcp.App.Services.ProxyHostService(
                path, null, new FakeSecretProtector());
            var document = new LitSSHmcp.Core.Services.Proxy.ProxyDocument();
            document.Options.AllowAnonymous = true;
            document.Options.UpstreamProxyPassword = "upstream-secret";

            await service.SaveAsync(document);
            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("enc:", json);
            Assert.DoesNotContain("upstream-secret", json);

            var loaded = await service.LoadAsync();
            Assert.Equal("upstream-secret", loaded.Options.UpstreamProxyPassword);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* ignore */ }
        }
    }

    private sealed class FakeSecretProtector : LitSSHmcp.Core.Services.Security.ISecretProtector
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
}
