using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class DpapiSecretProtectorTests
{
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public void Roundtrip_returns_original()
    {
        var secret = "p@ssw0rd-测试";
        var protectedValue = _protector.Protect(secret);

        Assert.Equal(secret, _protector.Unprotect(protectedValue));
    }

    [Fact]
    public void IsProtected_detects_prefix()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(_protector.IsProtected(_protector.Protect("x")));
        Assert.False(_protector.IsProtected("plain"));
        Assert.False(_protector.IsProtected(null));
    }

    [Fact]
    public void Protect_is_idempotent()
    {
        var once = _protector.Protect("x");
        Assert.Equal(once, _protector.Protect(once));
    }

    [Fact]
    public void Empty_values_pass_through()
    {
        Assert.Equal("", _protector.Protect(""));
        Assert.Null(_protector.Protect(null));
        Assert.Equal("", _protector.Unprotect(""));
        Assert.Null(_protector.Unprotect(null));
        Assert.Equal("plain", _protector.Unprotect("plain"));
    }
}
