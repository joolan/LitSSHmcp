using LitSSHmcp.App.Services.Toolbox;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TotpGeneratorTests
{
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    public void Matches_rfc6238_sha1_vectors(long unixSeconds, string expected)
    {
        var config = new PasswordTotpConfig
        {
            Secret = Secret,
            Algorithm = "SHA1",
            Digits = 8,
            Period = 30
        };

        var result = TotpGenerator.Generate(config, DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        Assert.Equal(expected, result.Code);
    }

    [Theory]
    [InlineData(0L, "755224")]
    [InlineData(1L, "287082")]
    [InlineData(2L, "359152")]
    [InlineData(5L, "254676")]
    [InlineData(9L, "520489")]
    public void Matches_rfc4226_hotp_vectors(long counter, string expected)
    {
        var config = new PasswordTotpConfig
        {
            Secret = Secret,
            Algorithm = "SHA1",
            Digits = 6,
            IsHotp = true
        };

        Assert.Equal(expected, TotpGenerator.GenerateHotp(config, counter));
    }

    [Fact]
    public void Parses_hotp_uri_with_counter()
    {
        const string uri = "otpauth://hotp/Test:user?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Test&counter=7";
        Assert.True(TotpGenerator.TryParseUri(uri, out var config));
        Assert.NotNull(config);
        Assert.True(config.IsHotp);
        Assert.Equal(7, config.Counter);
    }

    [Fact]
    public void Reports_remaining_seconds_and_progress()
    {
        var config = new PasswordTotpConfig { Secret = Secret, Digits = 6, Period = 30 };
        var result = TotpGenerator.Generate(config, DateTimeOffset.FromUnixTimeSeconds(59));

        Assert.Equal(1, result.RemainingSeconds);
        Assert.Equal(1d / 30d, result.Progress, 6);
        Assert.Equal(6, result.Code.Length);
    }

    [Fact]
    public void Parses_otpauth_uri_and_normalizes_secret()
    {
        const string uri = "otpauth://totp/GitHub:user%40example.com?secret=gezd-gnbv-gy3t-qojq-gezd-gnbv-gy3t-qojq&issuer=GitHub&algorithm=SHA256&digits=8&period=60";

        Assert.True(TotpGenerator.TryParseUri(uri, out var config));
        Assert.NotNull(config);
        Assert.Equal("GitHub", config.Issuer);
        Assert.Equal("user@example.com", config.Account);
        Assert.Equal("SHA256", config.Algorithm);
        Assert.Equal(8, config.Digits);
        Assert.Equal(60, config.Period);
        Assert.DoesNotContain("-", config.Secret);
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", config.Secret);
    }

    [Fact]
    public void Rejects_non_totp_uri_and_invalid_secret()
    {
        Assert.False(TotpGenerator.TryParseUri("https://example.com", out _));
        Assert.False(TotpGenerator.TryParseUri("otpauth://steam/x?secret=ABC", out _));
        Assert.False(TotpGenerator.TryParseUri("otpauth://totp/x", out _));

        var result = TotpGenerator.Generate(new PasswordTotpConfig { Secret = "" });
        Assert.Equal(string.Empty, result.Code);
    }
}
