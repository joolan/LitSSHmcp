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
}
