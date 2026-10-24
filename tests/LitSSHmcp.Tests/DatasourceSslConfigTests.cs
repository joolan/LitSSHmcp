using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Storage;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace LitSSHmcp.Tests;

public class DatasourceSslConfigTests
{
    private static DataSourceConfig Mysql() => new()
    {
        Type = "mysql",
        Host = "127.0.0.1",
        Port = 3306,
        Username = "u",
        Password = "p",
        DefaultDatabase = "db"
    };

    private static DataSourceConfig Postgres() => new()
    {
        Type = "postgres",
        Host = "127.0.0.1",
        Port = 5432,
        Username = "u",
        Password = "p",
        DefaultDatabase = "db"
    };

    [Fact]
    public void Mysql_defaults_keep_preferred_and_public_key_retrieval_off()
    {
        var builder = MySqlConnectionProvider.BuildConnectionString(Mysql(), "127.0.0.1", 40000);

        Assert.Equal(MySqlSslMode.Preferred, builder.SslMode);
        Assert.False(builder.AllowPublicKeyRetrieval);
        Assert.Equal(40000u, builder.Port);
        Assert.True(string.IsNullOrEmpty(builder.SslCa));
    }

    [Theory]
    [InlineData(DataSourceSslMode.None, MySqlSslMode.None)]
    [InlineData(DataSourceSslMode.Preferred, MySqlSslMode.Preferred)]
    [InlineData(DataSourceSslMode.Required, MySqlSslMode.Required)]
    [InlineData(DataSourceSslMode.VerifyCA, MySqlSslMode.VerifyCA)]
    [InlineData(DataSourceSslMode.VerifyFull, MySqlSslMode.VerifyFull)]
    public void Mysql_maps_ssl_mode(DataSourceSslMode mode, MySqlSslMode expected)
    {
        var ds = Mysql();
        ds.SslMode = mode;
        Assert.Equal(expected, MySqlConnectionProvider.BuildConnectionString(ds, "h", 3306).SslMode);
    }

    [Fact]
    public void Mysql_maps_public_key_retrieval_and_ca_file()
    {
        var ds = Mysql();
        ds.SslMode = DataSourceSslMode.VerifyCA;
        ds.AllowPublicKeyRetrieval = true;
        ds.SslCaFile = @"C:\certs\ca.pem";

        var builder = MySqlConnectionProvider.BuildConnectionString(ds, "h", 3306);

        Assert.True(builder.AllowPublicKeyRetrieval);
        Assert.Equal(@"C:\certs\ca.pem", builder.SslCa);
    }

    [Fact]
    public void Mysql_blank_ca_file_is_ignored()
    {
        var ds = Mysql();
        ds.SslCaFile = "   ";
        Assert.True(string.IsNullOrEmpty(MySqlConnectionProvider.BuildConnectionString(ds, "h", 3306).SslCa));
    }

    [Fact]
    public void Mysql_ca_file_ignored_unless_verify_mode()
    {
        var ds = Mysql();
        ds.SslCaFile = @"C:\certs\ca.pem";
        Assert.True(string.IsNullOrEmpty(MySqlConnectionProvider.BuildConnectionString(ds, "h", 3306).SslCa));
    }

    [Theory]
    [InlineData(DataSourceSslMode.None, Npgsql.SslMode.Disable)]
    [InlineData(DataSourceSslMode.Preferred, Npgsql.SslMode.Prefer)]
    [InlineData(DataSourceSslMode.Required, Npgsql.SslMode.Require)]
    [InlineData(DataSourceSslMode.VerifyCA, Npgsql.SslMode.VerifyCA)]
    [InlineData(DataSourceSslMode.VerifyFull, Npgsql.SslMode.VerifyFull)]
    public void Postgres_maps_ssl_mode(DataSourceSslMode mode, Npgsql.SslMode expected)
    {
        var ds = Postgres();
        ds.SslMode = mode;
        Assert.Equal(expected, PostgresConnectionProvider.BuildConnectionString(ds, "h", 5432).SslMode);
    }

    [Fact]
    public void Postgres_maps_root_certificate()
    {
        var ds = Postgres();
        ds.SslMode = DataSourceSslMode.VerifyCA;
        ds.SslCaFile = "/etc/ssl/ca.pem";

        var builder = PostgresConnectionProvider.BuildConnectionString(ds, "h", 5432);

        Assert.Equal("/etc/ssl/ca.pem", builder.RootCertificate);
    }

    [Fact]
    public void Json_without_ssl_fields_defaults_to_preferred()
    {
        var ds = JsonSerializer.Deserialize<DataSourceConfig>(
            """{"type":"mysql","host":"db.local"}""", AppConfigJson.Options)!;

        Assert.Equal(DataSourceSslMode.Preferred, ds.SslMode);
        Assert.False(ds.AllowPublicKeyRetrieval);
        Assert.Null(ds.SslCaFile);
    }

    [Fact]
    public void Json_ssl_fields_roundtrip()
    {
        var original = new DataSourceConfig
        {
            Type = "mysql",
            Host = "db.local",
            SslMode = DataSourceSslMode.VerifyCA,
            AllowPublicKeyRetrieval = true,
            SslCaFile = "/etc/ssl/ca.pem"
        };

        var json = JsonSerializer.Serialize(original, AppConfigJson.Options);
        Assert.Contains("\"sslMode\": \"VerifyCA\"", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<DataSourceConfig>(json, AppConfigJson.Options)!;
        Assert.Equal(DataSourceSslMode.VerifyCA, back.SslMode);
        Assert.True(back.AllowPublicKeyRetrieval);
        Assert.Equal("/etc/ssl/ca.pem", back.SslCaFile);
    }
}
