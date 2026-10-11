using System.IO;
using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.Tests;

public class SshKeyGeneratorTests
{
    [Fact]
    public void Generate_writes_private_and_public_key_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var key = SshKeyGenerator.Generate(dir, "id_test", "rsa", null, "test-comment");

            Assert.True(File.Exists(key.PrivateKeyPath));
            Assert.True(File.Exists(key.PublicKeyPath));
            Assert.StartsWith("ssh-", key.PublicKeyOpenSsh);
            Assert.Contains("test-comment", key.PublicKeyOpenSsh);
            Assert.NotEqual(string.Empty, File.ReadAllText(key.PrivateKeyPath).Trim());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
