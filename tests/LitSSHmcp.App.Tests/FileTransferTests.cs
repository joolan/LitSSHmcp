using System.IO;
using System.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.FileTransfer;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class FileTransferTests
{
    [Fact]
    public void Remote_paths_are_normalized()
    {
        Assert.Equal("/", FtpSession.NormalizeRemotePath(null));
        Assert.Equal("/var/www", FtpSession.NormalizeRemotePath(@"\var\\www"));
        Assert.Equal("/var/www", FtpSession.NormalizeRemotePath("var/www"));
    }

    [Fact]
    public void Anonymous_site_normalizes_username_and_clears_password()
    {
        var site = new FileTransferSiteConfig
        {
            Name = " demo ",
            Protocol = FileTransferProtocol.Ftps,
            FtpsMode = FtpsMode.Implicit,
            AuthType = FileTransferAuthType.Anonymous,
            Username = "",
            Password = "secret",
            Port = 0,
            Tags = new[] { " prod ", "prod", "" }
        };

        site.Normalize();

        Assert.Equal("demo", site.Name);
        Assert.Equal("anonymous", site.Username);
        Assert.Null(site.Password);
        Assert.Equal(990, site.Port);
        Assert.Equal(new[] { "prod" }, site.Tags);
    }

    [Fact]
    public void Advanced_site_fields_are_normalized()
    {
        var site = new FileTransferSiteConfig
        {
            TlsVersion = "",
            CertificatePins = new[] { "aa:bb cc", "AABBCC", "" },
            MaxConcurrentTransfers = 99,
            SpeedLimitKBps = -5,
            KeepAliveSeconds = -1,
            ProxyType = FtpProxyType.Socks5,
            ProxyPort = 0
        };

        site.Normalize();

        Assert.Equal("auto", site.TlsVersion);
        Assert.Equal(new[] { "AABBCC" }, site.CertificatePins);
        Assert.Equal(8, site.MaxConcurrentTransfers);
        Assert.Equal(0, site.SpeedLimitKBps);
        Assert.Equal(0, site.KeepAliveSeconds);
        Assert.Equal(1080, site.ProxyPort);
    }

    [Fact]
    public async Task File_transfer_sites_round_trip_with_encrypted_password()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-ftp-site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            var service = new ConfigService(path, new LitSSHmcp.Core.Services.Security.DpapiSecretProtector());
            var config = new AppConfig
            {
                FileTransferSites = new[]
                {
                    new FileTransferSiteConfig
                    {
                        Name = "demo",
                        Host = "ftp.example.com",
                        Username = "deploy",
                        Password = "plain-secret",
                        ClientCertificatePassword = "client-secret",
                        ProxyPassword = "proxy-secret"
                    }
                }
            };

            await service.SaveConfigAsync(config);
            var raw = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("plain-secret", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("client-secret", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("proxy-secret", raw, StringComparison.Ordinal);
            Assert.Contains("enc:", raw, StringComparison.Ordinal);

            var reloaded = await service.LoadConfigAsync();
            var site = Assert.Single(reloaded.FileTransferSites);
            Assert.Equal("plain-secret", site.Password);
            Assert.Equal("client-secret", site.ClientCertificatePassword);
            Assert.Equal("proxy-secret", site.ProxyPassword);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Legacy_config_adds_file_transfer_sites()
    {
        var config = new AppConfig
        {
            SchemaVersion = 7,
            FileTransferSites = null!
        };

        Assert.True(ConfigMigrator.Migrate(config));

        Assert.NotNull(config.FileTransferSites);
        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
    }

    [Fact]
    public void Manage_view_parses_xaml()
    {
        var error = RunSta(() => _ = new FileTransferManageView());
        Assert.Null(error);
    }

    [Fact]
    public void Site_edit_window_parses_xaml()
    {
        var error = RunSta(() => _ = new FtpSiteEditWindow(null));
        Assert.Null(error);
    }

    [Fact]
    public void Ftp_manager_window_parses_xaml()
    {
        var site = new FileTransferSiteConfig
        {
            Name = "demo",
            Host = "127.0.0.1",
            Port = 21,
            Username = "anonymous",
            AuthType = FileTransferAuthType.Anonymous
        };
        var vm = new FtpManagerViewModel(site, new FtpFileService(),
            new AuditLogService(), new UserDialogService());

        var error = RunSta(() =>
        {
            var window = new FtpManagerWindow(vm);
            window.Close();
        });
        Assert.Null(error);
    }

    private static Exception? RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }
}
