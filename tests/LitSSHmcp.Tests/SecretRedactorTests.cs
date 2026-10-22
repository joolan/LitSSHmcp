using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class SecretRedactorTests
{
    [Theory]
    [InlineData("curl --password hunter2 https://example", "hunter2")]
    [InlineData("mysql --password=hunter2 -e 'select 1'", "hunter2")]
    [InlineData("set PGPASSWORD=hunter2", "hunter2")]
    [InlineData("System.Data.SqlClient;Password=hunter2;User Id=sa", "hunter2")]
    [InlineData("https://user:hunter2@example.com/path", "hunter2")]
    [InlineData("Authorization: Bearer abcdefghijklmnop", "abcdefghijklmnop")]
    [InlineData("Cookie: session=abcdef123456", "abcdef123456")]
    [InlineData("token=abcdef123456&x=1", "abcdef123456")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz123456", "ghp_abcdefghijklmnopqrstuvwxyz123456")]
    public void Common_secret_shapes_are_masked(string text, string secret)
    {
        var redacted = SecretRedactor.Redact(text);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("******", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Private_key_block_is_replaced()
    {
        const string text = """
            before
            -----BEGIN RSA PRIVATE KEY-----
            MIIEowIBAAKCAQEA...
            -----END RSA PRIVATE KEY-----
            after
            """;

        var redacted = SecretRedactor.Redact(text);

        Assert.Contains("[REDACTED PRIVATE KEY]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA", redacted, StringComparison.Ordinal);
        Assert.Contains("before", redacted, StringComparison.Ordinal);
        Assert.Contains("after", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_secrets_are_replaced_and_short_values_ignored()
    {
        var redacted = SecretRedactor.RedactKnown("pwd=hunter2 and x=abc", new[] { "hunter2", "abc" });

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("x=abc", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_hash_is_stable_and_rule_matches_exact_command()
    {
        const string command = "git status --short";
        var hash1 = SecretRedactor.CommandHash(command);
        var hash2 = SecretRedactor.CommandHash("  " + command + "  ");

        Assert.Equal(hash1, hash2);
        Assert.NotEqual(hash1, SecretRedactor.CommandHash("git status"));

        var rule = AutoApprovedCommandRule.Create("curl --password hunter2 https://example");
        Assert.StartsWith(AutoApprovedCommandRule.Prefix, rule, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", rule, StringComparison.Ordinal);
        Assert.True(AutoApprovedCommandRule.Matches("curl --password hunter2 https://example", rule));
        Assert.False(AutoApprovedCommandRule.Matches("curl --password other https://example", rule));
        Assert.Contains("curl --password ******", AutoApprovedCommandRule.Display(rule), StringComparison.Ordinal);
    }
}
