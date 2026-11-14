using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class SecuritySettingsTrustedHashTests
{
    [Fact]
    public void Trusted_hash_rows_are_normalized_and_deduplicated()
    {
        var hash = ScriptRiskAnalyzer.ComputeSha256("echo ok\n");
        var rows = new[]
        {
            new SecuritySettingsViewModel.TrustedScriptHashRow
            {
                Sha256 = "sha256:" + hash.ToUpperInvariant(),
                Label = " official "
            }
        };

        var result = SecuritySettingsViewModel.ToTrustedScriptHashes(rows, out var error);

        Assert.Null(error);
        var item = Assert.Single(result!);
        Assert.Equal(hash, item.Sha256);
        Assert.Equal("official", item.Label);
    }

    [Fact]
    public void Invalid_or_short_trusted_hash_is_rejected()
    {
        var rows = new[]
        {
            new SecuritySettingsViewModel.TrustedScriptHashRow { Sha256 = "abc123" }
        };

        Assert.Null(SecuritySettingsViewModel.ToTrustedScriptHashes(rows, out var error));
        Assert.Contains("64", error);
    }

    [Fact]
    public void Duplicate_trusted_hash_is_rejected()
    {
        var hash = ScriptRiskAnalyzer.ComputeSha256("echo ok\n");
        var rows = new[]
        {
            new SecuritySettingsViewModel.TrustedScriptHashRow { Sha256 = hash },
            new SecuritySettingsViewModel.TrustedScriptHashRow { Sha256 = hash }
        };

        Assert.Null(SecuritySettingsViewModel.ToTrustedScriptHashes(rows, out var error));
        Assert.Contains("重复", error);
    }
}
