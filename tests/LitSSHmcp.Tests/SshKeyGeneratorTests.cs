using System.IO;
using System.Text;
using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.Tests;

public class SshKeyGeneratorTests
{
    [Fact]
    public void Generate_with_passphrase_does_not_write_secret_to_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-keygen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var passphrase = "unit-test-pass-123!";
            var generated = SshKeyGenerator.Generate(dir, "id_test", "ed25519", passphrase, "test");

            Assert.True(File.Exists(generated.PrivateKeyPath));
            Assert.True(File.Exists(generated.PublicKeyPath));

            var privateKey = File.ReadAllText(generated.PrivateKeyPath);
            Assert.DoesNotContain(passphrase, privateKey, StringComparison.Ordinal);

            if (!generated.UsedSshKeyGen)
                return;   // 系统没有 ssh-keygen 时走 RSA 回退，不校验 OpenSSH 加密头

            var payload = string.Concat(privateKey
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.StartsWith("-----", StringComparison.Ordinal))
                .Select(line => line.Trim()));
            var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(payload));
            Assert.Contains("bcrypt", decoded, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
