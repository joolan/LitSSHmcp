using System.Security.Cryptography;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.Tests;

public class SshClientFactoryTests
{
    [Fact]
    public void KeyFile_auth_loads_unencrypted_rsa_pem()
    {
        var path = Path.Combine(Path.GetTempPath(), "litssh-key-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            using (var rsa = RSA.Create(2048))
                File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());

            var server = new SshServerConfig
            {
                Host = "127.0.0.1",
                Port = 22,
                Username = "u",
                AuthType = AuthType.KeyFile,
                KeyFilePath = path
            };

            using var client = SshClientFactory.Create(server, knownHosts: null, hostKeyMode: SshHostKeyMode.Off);
            Assert.NotNull(client);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void KeyFile_auth_with_missing_file_throws_instead_of_falling_back()
    {
        var server = new SshServerConfig
        {
            Host = "127.0.0.1",
            Port = 22,
            Username = "u",
            AuthType = AuthType.KeyFile,
            KeyFilePath = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".key")
        };

        Assert.ThrowsAny<Exception>(() => SshClientFactory.Create(server, knownHosts: null, hostKeyMode: SshHostKeyMode.Off));
    }

    [Fact]
    public void Password_auth_builds_client()
    {
        var server = new SshServerConfig
        {
            Host = "127.0.0.1",
            Port = 22,
            Username = "u",
            AuthType = AuthType.Password,
            Password = "x"
        };

        using var client = SshClientFactory.Create(server, knownHosts: null, hostKeyMode: SshHostKeyMode.Off);
        Assert.NotNull(client);
    }
}
