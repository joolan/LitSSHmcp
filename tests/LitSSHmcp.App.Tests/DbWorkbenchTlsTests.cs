using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class DbWorkbenchTlsTests
{
    [Fact]
    public void Effective_database_copy_preserves_tls_settings()
    {
        var source = new DataSourceConfig
        {
            Id = "ds1",
            Name = "db",
            Type = "mysql",
            Host = "127.0.0.1",
            DefaultDatabase = "old",
            SslMode = DataSourceSslMode.None,
            AllowPublicKeyRetrieval = true,
            SslCaFile = "ca.pem"
        };

        var effective = DbWorkbenchViewModel.Effective(source, "new");

        Assert.Equal("new", effective.DefaultDatabase);
        Assert.Equal(DataSourceSslMode.None, effective.SslMode);
        Assert.True(effective.AllowPublicKeyRetrieval);
        Assert.Equal("ca.pem", effective.SslCaFile);
    }
}
