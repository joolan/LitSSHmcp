using System.Net;
using LitSSHmcp.Core.Services.Proxy;
using Xunit;

namespace LitSSHmcp.Tests.Proxy;

public class ProxySecurityTests
{
    [Fact]
    public void Account_password_hash_round_trips_and_rejects_wrong_password()
    {
        var account = new ProxyAccount { Username = "ops" };
        account.SetPassword("S3cret-pass:with-colon", 10_000);

        Assert.True(account.VerifyPassword("S3cret-pass:with-colon"));
        Assert.False(account.VerifyPassword("wrong"));
        Assert.False(string.IsNullOrWhiteSpace(account.PasswordSalt));
        Assert.False(string.IsNullOrWhiteSpace(account.PasswordHash));
        Assert.DoesNotContain("S3cret", account.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_account_never_verifies()
    {
        var account = new ProxyAccount { Username = "ops", Enabled = false };
        account.SetPassword("password", 10_000);

        Assert.False(account.VerifyPassword("password"));
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.50", true)]
    [InlineData("192.168.1.0/24", "192.168.2.50", false)]
    [InlineData("10.0.0.0/8", "10.20.30.40", true)]
    [InlineData("127.0.0.1/32", "127.0.0.1", true)]
    [InlineData("::1/128", "::1", true)]
    [InlineData("fc00::/7", "fd00::1", true)]
    public void Cidr_matching_works(string cidrText, string addressText, bool expected)
    {
        Assert.True(ProxyCidr.TryParse(cidrText, out var cidr));
        Assert.Equal(expected, cidr.Contains(IPAddress.Parse(addressText)));
    }

    [Fact]
    public void Client_whitelist_is_empty_by_default_and_fail_closed_when_invalid()
    {
        Assert.True(ProxyPolicy.IsClientAllowed(IPAddress.Parse("192.168.1.10"), Array.Empty<string>()));
        Assert.True(ProxyPolicy.IsClientAllowed(IPAddress.Parse("192.168.1.10"),
            new[] { "192.168.1.0/24" }));
        Assert.False(ProxyPolicy.IsClientAllowed(IPAddress.Parse("192.168.2.10"),
            new[] { "192.168.1.0/24" }));
        Assert.False(ProxyPolicy.IsClientAllowed(IPAddress.Parse("192.168.1.10"),
            new[] { "not-a-cidr" }));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("2001:db8::1")]
    public void Private_and_reserved_addresses_are_blocked(string address)
    {
        Assert.True(ProxyPolicy.IsPrivateOrReserved(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string address)
    {
        Assert.False(ProxyPolicy.IsPrivateOrReserved(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Target_policy_enforces_ports_and_private_destinations()
    {
        var options = new ProxyOptions
        {
            AllowedPorts = new List<int> { 80, 443 },
            BlockPrivateDestinations = true
        };

        var portRejected = await ProxyPolicy.EvaluateAsync("8.8.8.8", 8080, options);
        Assert.False(portRejected.Allowed);
        Assert.Contains("端口", portRejected.Reason);

        var privateRejected = await ProxyPolicy.EvaluateAsync("10.0.0.1", 80, options);
        Assert.False(privateRejected.Allowed);
        Assert.Contains("内网", privateRejected.Reason);

        options.BlockPrivateDestinations = false;
        var privateAllowed = await ProxyPolicy.EvaluateAsync("10.0.0.1", 80, options);
        Assert.True(privateAllowed.Allowed);
        Assert.Single(privateAllowed.Addresses);
    }

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("api.example.com", "example.com", true)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("api.example.com", "*.example.com", true)]
    [InlineData("evil-example.com", "example.com", false)]
    [InlineData("anything.example", "*", true)]
    public void Domain_matcher_handles_apex_subdomain_and_wildcard(
        string host, string pattern, bool expected)
    {
        Assert.Equal(expected, ProxyDomainMatcher.IsMatch(host, pattern));
    }

    [Fact]
    public void Domain_lists_apply_block_first_then_allow()
    {
        Assert.Null(ProxyDomainMatcher.Evaluate("api.example.com",
            new[] { "example.com" }, new[] { "blocked.example.com" }));
        Assert.Contains("白名单", ProxyDomainMatcher.Evaluate("other.com",
            new[] { "example.com" }, null));
        Assert.Contains("黑名单", ProxyDomainMatcher.Evaluate("tracker.example.com",
            null, new[] { "*.example.com" }));
    }

    [Fact]
    public async Task Target_policy_honors_domain_allow_and_block_lists()
    {
        var options = new ProxyOptions
        {
            AllowedPorts = new List<int> { 443 },
            BlockPrivateDestinations = false,
            AllowedDomains = new List<string> { "example.com", "8.8.8.8" },
            BlockedDomains = new List<string> { "*.blocked.example.com" }
        };

        var notAllowed = await ProxyPolicy.EvaluateAsync("other.com", 443, options);
        Assert.False(notAllowed.Allowed);
        Assert.Contains("白名单", notAllowed.Reason);

        var blocked = await ProxyPolicy.EvaluateAsync("a.blocked.example.com", 443, options);
        Assert.False(blocked.Allowed);
        Assert.Contains("黑名单", blocked.Reason);

        var allowed = await ProxyPolicy.EvaluateAsync("8.8.8.8", 443, options);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void Auth_failure_tracker_blocks_after_limit_and_resets()
    {
        var tracker = new ProxyAuthFailureTracker();
        ProxyAuthFailureResult result = null!;
        for (var i = 0; i < 3; i++)
            result = tracker.RecordFailure("192.168.1.10", 3, 300, 300);

        Assert.True(result.Blocked);
        Assert.True(result.ShouldAudit);
        Assert.True(tracker.IsBlocked("192.168.1.10", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        tracker.Reset("192.168.1.10");
        Assert.False(tracker.IsBlocked("192.168.1.10", out _));
    }

    [Fact]
    public void Auth_failure_tracker_can_be_disabled()
    {
        var tracker = new ProxyAuthFailureTracker();
        ProxyAuthFailureResult result = null!;
        for (var i = 0; i < 20; i++)
            result = tracker.RecordFailure("192.168.1.10", 0, 300, 300);
        Assert.False(result.Blocked);
        Assert.False(tracker.IsBlocked("192.168.1.10", out _));
    }

    [Fact]
    public async Task Store_round_trips_document()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "litssh-proxy-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = System.IO.Path.Combine(directory, "http-socks-proxy.json");
            var store = new ProxyStore(path);
            var account = new ProxyAccount { Username = "ops" };
            account.SetPassword("password", 10_000);
            var document = new ProxyDocument
            {
                Options = new ProxyOptions
                {
                    HttpPort = 18888,
                    Socks5Port = 11080,
                    AllowAnonymous = true,
                    AllowedPorts = new List<int> { 80, 443, 8443 },
                    AllowedDomains = new List<string> { "example.com", "*.example.org" },
                    BlockedDomains = new List<string> { "*.ads.example" },
                    AuthFailureLimit = 5,
                    AuthFailureWindowSeconds = 120,
                    AuthFailureBlockSeconds = 600
                },
                Accounts = new List<ProxyAccount> { account }
            };

            await store.SaveAsync(document);
            var loaded = await store.LoadAsync();

            Assert.True(System.IO.File.Exists(path));
            Assert.False(System.IO.File.Exists(path + ".bak")); // 首次保存没有旧文件可备份
            Assert.Equal(18888, loaded.Options.HttpPort);
            Assert.Equal(11080, loaded.Options.Socks5Port);
            Assert.True(loaded.Options.AllowAnonymous);
            Assert.Contains("example.com", loaded.Options.AllowedDomains);
            Assert.Contains("*.ads.example", loaded.Options.BlockedDomains);
            Assert.Equal(5, loaded.Options.AuthFailureLimit);
            Assert.Equal(600, loaded.Options.AuthFailureBlockSeconds);
            Assert.Single(loaded.Accounts);
            Assert.Equal("ops", loaded.Accounts[0].Username);
            Assert.True(loaded.Accounts[0].VerifyPassword("password"));
        }
        finally
        {
            try { System.IO.Directory.Delete(directory, recursive: true); } catch { /* ignore */ }
        }
    }
}
