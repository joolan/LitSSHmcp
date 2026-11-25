using System.IO;
using System.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Backup;
using LitSSHmcp.App.Views.Tools;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.DnsServer;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class DnsConfigurationTests
{
    [Fact]
    public void Backup_catalog_includes_dns_config()
    {
        var descriptor = Assert.Single(BackupItemCatalog.Descriptors,
            item => item.Id == "dns-config");
        Assert.Equal("dns-server.json", Path.GetFileName(
            descriptor.ResolvePath(new AppConfig(), @"C:\data")));

        var item = Assert.Single(
            BackupItemCatalog.Build(new AppConfig(), @"C:\data", new[] { "dns-config" }));
        Assert.Equal(@"C:\data\dns-server.json", item.Path);
        Assert.Equal(BackupItemKind.File, item.Descriptor.Kind);
    }

    [Fact]
    public void Dns_tool_window_parses_xaml()
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { _ = new DnsToolWindow(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(captured);
    }

    [Fact]
    public void Dns_record_edit_window_parses_xaml()
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { _ = new DnsRecordEditWindow(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(captured);
    }

    [Fact]
    public void Firewall_commands_cover_udp_and_tcp_ports()
    {
        var service = new DnsFirewallService();
        var options = new DnsOptions
        {
            Port = 5353,
            UdpEnabled = true,
            TcpEnabled = true
        };
        var commands = service.BuildAddCommands(options);
        Assert.Contains("protocol=UDP localport=5353", commands);
        Assert.Contains("protocol=TCP localport=5353", commands);
        Assert.Contains("profile=private", commands);

        var remove = service.BuildRemoveCommands(options);
        Assert.Contains("delete rule", remove);
        Assert.DoesNotContain("add rule", remove);
    }

    [Fact]
    public async Task Dns_host_service_persists_document_and_rejects_invalid_config()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "litssh-dns-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "dns-server.json");
            using var service = new DnsHostService(path);
            var document = new DnsDocument();
            document.Options.BindAddress = "127.0.0.1";
            document.Options.AllowedDomains.Add("lab.test");
            document.Options.ReuseUpstreamConnections = false;
            document.Options.RrlEnabled = true;
            document.Options.RrlResponsesPerSecond = 123;
            document.Options.RrlBurst = 456;
            document.Options.LocalRecords.Add(new DnsLocalRecord
            {
                Name = "nas.lab.test", Type = "A", Value = "192.168.1.30"
            });

            await service.SaveAsync(document);
            Assert.True(File.Exists(path));

            var loaded = await service.LoadAsync();
            Assert.Equal(new[] { "lab.test" }, loaded.Options.AllowedDomains);
            Assert.Single(loaded.Options.LocalRecords);
            Assert.False(loaded.Options.ReuseUpstreamConnections);
            Assert.True(loaded.Options.RrlEnabled);
            Assert.Equal(123, loaded.Options.RrlResponsesPerSecond);
            Assert.Equal(456, loaded.Options.RrlBurst);

            var invalid = new DnsDocument();
            invalid.Options.Upstreams.Add("not-an-upstream");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SaveAsync(invalid));
            Assert.False(service.Status.Running);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* ignore */ }
        }
    }
}
