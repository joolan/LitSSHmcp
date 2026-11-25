using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using LitSSHmcp.Core.Services.DnsServer;
using LitSSHmcp.Core.Services.Proxy;
using Xunit;

namespace LitSSHmcp.Tests.DnsServer;

public class DnsServerTests
{
    [Fact]
    public void Wire_round_trips_question_and_answers()
    {
        var query = DnsTestPacket.Query("nas.lab", 1);
        Assert.True(DnsWire.TryParseMessage(query, out var layout));
        Assert.Equal(0x1234, layout.Id);
        Assert.Equal("nas.lab", layout.Question.Name);
        Assert.Equal(1, layout.Question.Type);
        Assert.False(layout.IsResponse);

        var response = DnsWire.BuildResponse(query, layout.Question,
            new[] { new DnsAnswerRecord(1, 120, new byte[] { 192, 168, 1, 20 }) },
            authoritative: true);
        Assert.True(DnsWire.TryParseMessage(response, out var parsed));
        Assert.True(parsed.IsResponse);
        Assert.True(parsed.IsAuthoritative);
        Assert.Equal(0x1234, parsed.Id);
        Assert.Equal("nas.lab", parsed.Question.Name);
        var answer = Assert.Single(parsed.Answers);
        Assert.Equal(1, answer.Type);
        Assert.Equal(120u, answer.Ttl);
        Assert.Equal(new byte[] { 192, 168, 1, 20 },
            response.AsSpan(answer.RdataOffset, answer.RdataLength).ToArray());
    }

    [Fact]
    public void Wire_reencodes_compressed_question_when_building_response()
    {
        // 构造一个带压缩指针的问题段：QNAME 指向报文内偏移 18。
        var message = new byte[64];
        DnsWire.WriteU16(message, 0, 0x7777);
        DnsWire.WriteU16(message, 2, 0x0100);
        DnsWire.WriteU16(message, 4, 1);
        message[12] = 0xC0;
        message[13] = 18;
        DnsWire.WriteU16(message, 14, 1);
        DnsWire.WriteU16(message, 16, 1);
        message[18] = 3;
        "abc"u8.CopyTo(message.AsSpan(19));
        message[22] = 0;

        Assert.True(DnsWire.TryParseMessage(message.AsSpan(0, 23), out var layout));
        Assert.Equal("abc", layout.Question.Name);

        var response = DnsWire.BuildResponse(message.AsSpan(0, 23).ToArray(), layout.Question,
            Array.Empty<DnsAnswerRecord>());
        Assert.True(DnsWire.TryParseMessage(response, out var parsed));
        Assert.Equal("abc", parsed.Question.Name);
        Assert.Empty(parsed.Answers);
    }

    [Fact]
    public void Wire_adjusts_ttls_and_handles_underflow()
    {
        var query = DnsTestPacket.Query("a.test", 1);
        Assert.True(DnsWire.TryParseMessage(query, out var layout));
        var response = DnsWire.BuildResponse(query, layout.Question,
            new[]
            {
                new DnsAnswerRecord(1, 30, new byte[] { 1, 1, 1, 1 }),
                new DnsAnswerRecord(1, 5, new byte[] { 1, 1, 1, 2 })
            });
        Assert.True(DnsWire.TryParseMessage(response, out var parsed));
        DnsWire.AdjustTtls(response, parsed.TtlOffsets, 10);
        Assert.True(DnsWire.TryParseMessage(response, out var adjusted));
        Assert.Equal(20u, adjusted.Answers[0].Ttl);
        Assert.Equal(0u, adjusted.Answers[1].Ttl);

        DnsWire.AdjustTtls(response, adjusted.TtlOffsets, 0, 42);
        Assert.True(DnsWire.TryParseMessage(response, out var overridden));
        Assert.All(overridden.Answers, record => Assert.Equal(42u, record.Ttl));
    }

    [Fact]
    public void Wire_builds_truncated_and_error_responses()
    {
        var query = DnsTestPacket.Query("big.test", 16);
        Assert.True(DnsWire.TryParseMessage(query, out var layout));

        var truncated = DnsWire.BuildTruncated(query, layout.Question);
        Assert.True(DnsWire.TryParseMessage(truncated, out var truncatedLayout));
        Assert.True(truncatedLayout.Truncated);

        var error = DnsWire.BuildError(query, layout.Question, 3);
        Assert.True(DnsWire.TryParseMessage(error, out var errorLayout));
        Assert.Equal(3, errorLayout.Rcode);
        Assert.Equal("big.test", errorLayout.Question.Name);
    }

    [Fact]
    public void Wire_rejects_unsupported_question_count()
    {
        var query = DnsTestPacket.Query("a.test", 1);
        DnsWire.WriteU16(query, 4, 2);
        Assert.False(DnsWire.TryParseMessage(query, out _));
    }

    [Theory]
    [InlineData("192.168.1.10", "udp", 53)]
    [InlineData("192.168.1.10:5353", "udp", 5353)]
    [InlineData("tcp://1.1.1.1", "tcp", 53)]
    [InlineData("udp://223.5.5.5:5353", "udp", 5353)]
    [InlineData("[2001:db8::1]:53", "udp", 53)]
    [InlineData("2001:db8::1", "udp", 53)]
    public void Upstream_endpoint_parses_supported_forms(string text, string protocol, int port)
    {
        Assert.True(DnsUpstreamEndpoint.TryParse(text, out var endpoint));
        Assert.Equal(protocol, endpoint.Protocol);
        Assert.Equal(port, endpoint.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dns.example.com")]
    [InlineData("1.1.1.1:0")]
    [InlineData("1.1.1.1:70000")]
    public void Upstream_endpoint_rejects_invalid_forms(string text) =>
        Assert.False(DnsUpstreamEndpoint.TryParse(text, out _));

    [Fact]
    public void Options_validate_reports_invalid_values()
    {
        var options = new DnsOptions
        {
            Port = 70000,
            UdpEnabled = false,
            TcpEnabled = false,
            Upstreams = new List<string> { "not-an-ip" },
            AllowedClientCidrs = new List<string> { "300.1.1.1/24" },
            BlockedDomains = new List<string> { "bad domain" }
        };
        var errors = options.Validate();
        Assert.Contains(errors, error => error.Contains("端口"));
        Assert.Contains(errors, error => error.Contains("至少启用一个"));
        Assert.Contains(errors, error => error.Contains("上游 DNS"));
        Assert.Contains(errors, error => error.Contains("客户端白名单"));
        Assert.Contains(errors, error => error.Contains("域名规则"));
    }

    [Fact]
    public void Options_normalize_trims_and_deduplicates()
    {
        var options = new DnsOptions
        {
            BindAddress = "  ",
            Upstreams = new List<string> { " 1.1.1.1 ", "1.1.1.1" },
            BlockedDomains = new List<string> { " Ads.Test ", "ads.test." },
            AllowedClientCidrs = new List<string> { " 192.168.1.0/24 ", "" },
            RrlResponsesPerSecond = 0,
            RrlBurst = 999_999
        };
        options.Normalize();

        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.Equal(new[] { "1.1.1.1" }, options.Upstreams);
        Assert.Equal(new[] { "ads.test" }, options.BlockedDomains);
        Assert.Equal(new[] { "192.168.1.0/24" }, options.AllowedClientCidrs);
        Assert.Equal(1, options.RrlResponsesPerSecond);
        Assert.Equal(200_000, options.RrlBurst);
    }

    [Fact]
    public void Client_policy_limits_to_loopback_and_private_by_default()
    {
        var options = new DnsOptions();
        Assert.True(DnsClientPolicy.IsAllowed(IPAddress.Loopback, options));
        Assert.True(DnsClientPolicy.IsAllowed(IPAddress.Parse("192.168.1.50"), options));
        Assert.True(DnsClientPolicy.IsAllowed(IPAddress.Parse("10.0.0.8"), options));
        Assert.True(DnsClientPolicy.IsAllowed(IPAddress.Parse("fd00::1"), options));
        Assert.False(DnsClientPolicy.IsAllowed(IPAddress.Parse("8.8.8.8"), options));

        options.AllowPublicClients = true;
        Assert.True(DnsClientPolicy.IsAllowed(IPAddress.Parse("8.8.8.8"), options));

        options.AllowPublicClients = false;
        options.AllowedClientCidrs = new List<string> { "203.0.113.0/24" };
        Assert.True(DnsClientPolicy.IsAllowed(IPAddress.Parse("203.0.113.5"), options));
        Assert.False(DnsClientPolicy.IsAllowed(IPAddress.Parse("192.168.1.50"), options));
    }

    [Fact]
    public void Domain_policy_blocks_first_then_applies_whitelist()
    {
        Assert.Null(DnsDomainPolicy.Evaluate("example.com",
            Array.Empty<string>(), Array.Empty<string>()));
        Assert.NotNull(DnsDomainPolicy.Evaluate("ads.example.com",
            Array.Empty<string>(), new[] { "*.example.com" }));
        Assert.NotNull(DnsDomainPolicy.Evaluate("other.test",
            new[] { "example.com" }, Array.Empty<string>()));
        Assert.Null(DnsDomainPolicy.Evaluate("api.example.com",
            new[] { "example.com" }, Array.Empty<string>()));
        Assert.NotNull(DnsDomainPolicy.Evaluate("ads.example.com",
            new[] { "*.example.com" }, new[] { "ads.example.com" }));
    }

    [Fact]
    public void Local_resolver_matches_exact_wildcard_and_nodata()
    {
        var resolver = new DnsLocalResolver(new[]
        {
            new DnsLocalRecord { Name = "nas.lab", Type = "A", Value = "192.168.1.20" },
            new DnsLocalRecord { Name = "*.lab", Type = "A", Value = "192.168.1.21" },
            new DnsLocalRecord { Name = "www.lab", Type = "CNAME", Value = "nas.lab" },
            new DnsLocalRecord { Name = "20.1.168.192.in-addr.arpa", Type = "PTR", Value = "nas.lab" },
            new DnsLocalRecord { Name = "disabled.lab", Type = "A", Value = "192.168.1.99", Enabled = false }
        });

        var exact = resolver.Lookup("nas.lab", 1);
        Assert.True(exact.Matched);
        Assert.Single(exact.Records);

        var wildcard = resolver.Lookup("printer.lab", 1);
        Assert.True(wildcard.Matched);
        Assert.Single(wildcard.Records);
        Assert.Equal("192.168.1.21", wildcard.Records[0].Value);

        // 名称存在但类型不匹配：返回 NODATA，继续询问也不会泄露到上游。
        var noData = resolver.Lookup("nas.lab", 28);
        Assert.True(noData.Matched);
        Assert.Empty(noData.Records);

        Assert.True(resolver.Lookup("printer.lab", 12).Matched);
        Assert.True(resolver.Lookup("www.lab", 1).Matched);
        Assert.DoesNotContain(resolver.Lookup("disabled.lab", 1).Records,
            record => record.Value == "192.168.1.99");
        Assert.False(resolver.Lookup("unknown.other", 1).Matched);
    }

    [Fact]
    public void Hosts_file_parses_comments_aliases_and_reports_errors()
    {
        var result = DnsHostsFile.Parse(
            "# comment\n" +
            "192.168.1.20 nas.lab nas\n" +
            "2001:db8::20 ipv6.lab\n" +
            "not-an-ip host.lab\n" +
            "192.168.1.21\n" +
            "192.168.1.22 bad..name\n");

        Assert.Equal(3, result.Records.Count);
        Assert.Contains(result.Records, record =>
            record.Name == "nas.lab" && record.Type == "A" && record.Value == "192.168.1.20");
        Assert.Contains(result.Records, record =>
            record.Name == "nas" && record.Type == "A");
        Assert.Contains(result.Records, record =>
            record.Name == "ipv6.lab" && record.Type == "AAAA");
        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public void Hosts_file_exports_only_enabled_a_and_aaaa_records()
    {
        var text = DnsHostsFile.Export(new[]
        {
            new DnsLocalRecord { Name = "nas.lab", Type = "A", Value = "192.168.1.20" },
            new DnsLocalRecord { Name = "v6.lab", Type = "AAAA", Value = "2001:db8::20" },
            new DnsLocalRecord { Name = "alias.lab", Type = "CNAME", Value = "nas.lab" },
            new DnsLocalRecord
            {
                Name = "off.lab", Type = "A", Value = "192.168.1.30", Enabled = false
            }
        });

        Assert.Contains("192.168.1.20\tnas.lab", text);
        Assert.Contains("2001:db8::20\tv6.lab", text);
        Assert.DoesNotContain("alias.lab", text);
        Assert.DoesNotContain("off.lab", text);
    }

    [Fact]
    public void Cache_separates_fresh_and_stale_entries()
    {
        var cache = new DnsCache(100);
        var now = DateTimeOffset.UtcNow;
        cache.Set("k", new DnsCacheEntry
        {
            Response = new byte[12],
            TtlOffsets = Array.Empty<int>(),
            StoredAt = now.AddMinutes(-5),
            ExpiresAt = now.AddSeconds(-1),
            StaleUntil = now.AddMinutes(5),
            Negative = false
        });

        Assert.False(cache.TryGetFresh("k", now, out _));
        Assert.True(cache.TryGetStale("k", now, out _));

        cache.Set("k2", new DnsCacheEntry
        {
            Response = new byte[12],
            TtlOffsets = Array.Empty<int>(),
            StoredAt = now,
            ExpiresAt = now.AddSeconds(30),
            StaleUntil = now.AddMinutes(5),
            Negative = false
        });
        Assert.True(cache.TryGetFresh("k2", now, out _));
        Assert.False(cache.TryGetStale("k2", now, out _));

        cache.Set("expired", new DnsCacheEntry
        {
            Response = new byte[12],
            TtlOffsets = Array.Empty<int>(),
            StoredAt = now.AddHours(-2),
            ExpiresAt = now.AddHours(-1),
            StaleUntil = now.AddMinutes(-30),
            Negative = false
        });
        Assert.False(cache.TryGetStale("expired", now, out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task Store_round_trips_and_falls_back_to_backup()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "litssh-dns-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "dns-server.json");
            var store = new DnsStore(path);
            var first = new DnsDocument();
            first.Options.Upstreams.Add("1.1.1.1");
            first.Options.LocalRecords.Add(new DnsLocalRecord
            {
                Name = "nas.lab", Type = "A", Value = "192.168.1.20"
            });
            await store.SaveAsync(first);

            var second = new DnsDocument();
            second.Options.Upstreams.Add("223.5.5.5");
            await store.SaveAsync(second);
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".bak"));

            var loaded = await store.LoadAsync();
            Assert.Equal(new[] { "223.5.5.5" }, loaded.Options.Upstreams);

            await File.WriteAllTextAsync(path, "{ broken json");
            var fallback = await store.LoadAsync();
            Assert.Equal(new[] { "1.1.1.1" }, fallback.Options.Upstreams);
            Assert.Single(fallback.Options.LocalRecords);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Host_answers_local_records_and_caches_upstream()
    {
        await using var upstream = new FakeDnsUpstream();
        await using var host = new DnsServerHost();
        var options = new DnsOptions
        {
            BindAddress = "127.0.0.1",
            Port = 0,
            UdpEnabled = true,
            TcpEnabled = true,
            Upstreams = new List<string> { $"127.0.0.1:{upstream.Port}" },
            TemporaryEnableMinutes = 0,
            UpstreamTimeoutMs = 2000,
            LocalRecords = new List<DnsLocalRecord>
            {
                new() { Name = "nas.lab", Type = "A", Value = "192.168.1.20", Ttl = 300 }
            }
        };

        await host.StartAsync(options);
        try
        {
            var status = host.Status;
            Assert.True(status.Running);
            var port = IPEndPoint.Parse(status.UdpEndpoint!).Port;

            var localResponse = await DnsTestPacket.QueryUdpAsync(port, "nas.lab", 1);
            Assert.Equal(IPAddress.Parse("192.168.1.20"),
                DnsTestPacket.FirstAddress(localResponse));
            Assert.Equal(0, upstream.Requests);

            var first = await DnsTestPacket.QueryUdpAsync(port, "example.com", 1);
            Assert.Equal(IPAddress.Parse("203.0.113.9"), DnsTestPacket.FirstAddress(first));
            Assert.Equal(1, upstream.Requests);

            var second = await DnsTestPacket.QueryUdpAsync(port, "example.com", 1);
            Assert.Equal(IPAddress.Parse("203.0.113.9"), DnsTestPacket.FirstAddress(second));
            Assert.Equal(1, upstream.Requests);
            Assert.Equal(1, host.Status.CacheHits);
            Assert.Equal(DnsCacheState.Fresh, host.GetCacheState("example.com", 1));
            Assert.Equal(DnsCacheState.None, host.GetCacheState("other.example.com", 1));

            host.ClearCache();
            Assert.Equal(0, host.CacheCount);
            Assert.Equal(DnsCacheState.None, host.GetCacheState("example.com", 1));

            var logs = host.Log.Snapshot();
            Assert.Contains(logs, entry => entry.Result == "缓存命中" && entry.Question == "example.com");
            Assert.Contains(logs, entry => entry.Result == "本地" && entry.Question == "nas.lab");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void Response_rate_limiter_uses_token_bucket_and_subnet_keys()
    {
        var limiter = new DnsResponseRateLimiter();
        limiter.Configure(enabled: false, responsesPerSecond: 1, burst: 1);
        Assert.True(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("a"));

        limiter.Configure(enabled: true, responsesPerSecond: 1, burst: 1);
        Assert.True(limiter.TryAcquire("a"));
        Assert.False(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("b"));

        Assert.Equal("192.168.1.0/24",
            DnsResponseRateLimiter.SubnetKey(IPAddress.Parse("192.168.1.77")));
        Assert.Equal("2001:db8::/56",
            DnsResponseRateLimiter.SubnetKey(IPAddress.Parse("2001:db8:0:1::5")));
    }

    [Fact]
    public async Task Host_coalesces_concurrent_identical_upstream_queries()
    {
        await using var upstream = new FakeDnsUpstream(delayMs: 300);
        await using var host = new DnsServerHost();
        await host.StartAsync(new DnsOptions
        {
            BindAddress = "127.0.0.1",
            Port = 0,
            UdpEnabled = true,
            TcpEnabled = false,
            Upstreams = new List<string> { $"127.0.0.1:{upstream.Port}" },
            TemporaryEnableMinutes = 0,
            UpstreamTimeoutMs = 3000,
            Cache = new DnsCacheOptions { Enabled = false },
            MaxInFlight = 100
        });

        try
        {
            var port = IPEndPoint.Parse(host.Status.UdpEndpoint!).Port;
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => DnsTestPacket.QueryUdpAsync(port, "same.example", 1))
                .ToArray();
            var responses = await Task.WhenAll(tasks);

            Assert.All(responses, response =>
                Assert.Equal(IPAddress.Parse("203.0.113.9"),
                    DnsTestPacket.FirstAddress(response)));
            Assert.Equal(1, upstream.Requests);
            Assert.Equal(9, host.Status.Coalesced);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_rate_limits_udp_responses_but_not_tcp()
    {
        await using var host = new DnsServerHost();
        await host.StartAsync(new DnsOptions
        {
            BindAddress = "127.0.0.1",
            Port = 0,
            UdpEnabled = true,
            TcpEnabled = true,
            TemporaryEnableMinutes = 0,
            Upstreams = new List<string> { "127.0.0.1:9" },
            RrlEnabled = true,
            RrlResponsesPerSecond = 1,
            RrlBurst = 1,
            LocalRecords = new List<DnsLocalRecord>
            {
                new() { Name = "rrl.lab", Type = "A", Value = "10.0.0.1" }
            }
        });

        try
        {
            var port = IPEndPoint.Parse(host.Status.UdpEndpoint!).Port;
            var first = await DnsTestPacket.QueryUdpAsync(port, "rrl.lab", 1);
            Assert.Equal(IPAddress.Parse("10.0.0.1"), DnsTestPacket.FirstAddress(first));

            var second = await DnsTestPacket.QueryUdpAsync(port, "rrl.lab", 1);
            Assert.True(DnsWire.TryParseMessage(second, out var limited));
            Assert.True(limited.Truncated);
            Assert.Empty(limited.Answers);

            var tcp = await DnsTestPacket.QueryTcpAsync(host.Status.TcpEndpoint!, "rrl.lab", 1);
            Assert.Equal(IPAddress.Parse("10.0.0.1"), DnsTestPacket.FirstAddress(tcp));

            Assert.Contains(host.Log.Snapshot(), entry => entry.Result == "限速");
            Assert.Equal(1, host.Status.RateLimited);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_reuses_upstream_udp_sockets()
    {
        await using var upstream = new FakeDnsUpstream();
        await using var host = new DnsServerHost();
        await host.StartAsync(new DnsOptions
        {
            BindAddress = "127.0.0.1",
            Port = 0,
            UdpEnabled = true,
            TcpEnabled = false,
            TemporaryEnableMinutes = 0,
            UpstreamTimeoutMs = 3000,
            Upstreams = new List<string> { $"127.0.0.1:{upstream.Port}" },
            Cache = new DnsCacheOptions { Enabled = false }
        });

        try
        {
            var port = IPEndPoint.Parse(host.Status.UdpEndpoint!).Port;
            foreach (var name in new[] { "one.example", "two.example" })
            {
                var response = await DnsTestPacket.QueryUdpAsync(port, name, 1);
                Assert.Equal(IPAddress.Parse("203.0.113.9"),
                    DnsTestPacket.FirstAddress(response));
            }

            var ports = upstream.SourcePorts;
            Assert.Equal(2, ports.Length);
            Assert.Equal(ports[0], ports[1]);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_reuses_upstream_tcp_connections()
    {
        await using var upstream = new FakeTcpDnsUpstream();
        await using var host = new DnsServerHost();
        await host.StartAsync(new DnsOptions
        {
            BindAddress = "127.0.0.1",
            Port = 0,
            UdpEnabled = false,
            TcpEnabled = true,
            TemporaryEnableMinutes = 0,
            UpstreamTimeoutMs = 3000,
            Upstreams = new List<string> { $"tcp://127.0.0.1:{upstream.Port}" },
            Cache = new DnsCacheOptions { Enabled = false }
        });

        try
        {
            for (var i = 0; i < 2; i++)
            {
                var response = await DnsTestPacket.QueryTcpAsync(
                    host.Status.TcpEndpoint!, "example.com", 1);
                Assert.Equal(IPAddress.Parse("203.0.113.10"),
                    DnsTestPacket.FirstAddress(response));
            }

            Assert.Equal(1, upstream.Connections);
            Assert.Equal(2, upstream.Requests);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_can_disable_upstream_tcp_connection_reuse()
    {
        await using var upstream = new FakeTcpDnsUpstream();
        await using var host = new DnsServerHost();
        await host.StartAsync(new DnsOptions
        {
            BindAddress = "127.0.0.1",
            Port = 0,
            UdpEnabled = false,
            TcpEnabled = true,
            TemporaryEnableMinutes = 0,
            UpstreamTimeoutMs = 3000,
            ReuseUpstreamConnections = false,
            Upstreams = new List<string> { $"tcp://127.0.0.1:{upstream.Port}" },
            Cache = new DnsCacheOptions { Enabled = false }
        });

        try
        {
            for (var i = 0; i < 2; i++)
            {
                var response = await DnsTestPacket.QueryTcpAsync(
                    host.Status.TcpEndpoint!, "example.com", 1);
                Assert.Equal(IPAddress.Parse("203.0.113.10"),
                    DnsTestPacket.FirstAddress(response));
            }

            Assert.Equal(2, upstream.Connections);
            Assert.Equal(2, upstream.Requests);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_blocks_domains_and_returns_zero_for_a_queries()
    {
        await using var host = new DnsServerHost();
        var options = new DnsOptions
        {
            BindAddress = "127.0.0.1",
            Port = 0,
            UdpEnabled = true,
            TcpEnabled = false,
            BlockMode = "zero",
            BlockedDomains = new List<string> { "ads.test" },
            TemporaryEnableMinutes = 0,
            Upstreams = new List<string> { "127.0.0.1:9" }
        };

        await host.StartAsync(options);
        try
        {
            var port = IPEndPoint.Parse(host.Status.UdpEndpoint!).Port;
            var response = await DnsTestPacket.QueryUdpAsync(port, "ads.test", 1);
            Assert.Equal(IPAddress.Any, DnsTestPacket.FirstAddress(response));
            Assert.Equal(1, host.Status.Blocked);

            var nxdomainMode = new DnsOptions
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                UdpEnabled = true,
                TcpEnabled = false,
                BlockedDomains = new List<string> { "ads.test" },
                TemporaryEnableMinutes = 0,
                Upstreams = new List<string> { "127.0.0.1:9" }
            };
            await host.StartAsync(nxdomainMode);
            port = IPEndPoint.Parse(host.Status.UdpEndpoint!).Port;
            response = await DnsTestPacket.QueryUdpAsync(port, "ads.test", 1);
            Assert.True(DnsWire.TryParseMessage(response, out var layout));
            Assert.Equal(3, layout.Rcode);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_serves_tcp_queries_with_length_prefix()
    {
        await using var host = new DnsServerHost();
        await host.StartAsync(new DnsOptions
        {
            BindAddress = "127.0.0.1",
            Port = 0,
            UdpEnabled = true,
            TcpEnabled = true,
            TemporaryEnableMinutes = 0,
            Upstreams = new List<string> { "127.0.0.1:9" },
            LocalRecords = new List<DnsLocalRecord>
            {
                new() { Name = "tcp.lab", Type = "A", Value = "10.1.2.3" }
            }
        });
        try
        {
            var endpoint = IPEndPoint.Parse(host.Status.TcpEndpoint!);
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint);
            var stream = client.GetStream();
            var query = DnsTestPacket.Query("tcp.lab", 1);
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)query.Length);
            await stream.WriteAsync(length);
            await stream.WriteAsync(query);

            var responseLength = new byte[2];
            await stream.ReadExactlyAsync(responseLength);
            var response = new byte[BinaryPrimitives.ReadUInt16BigEndian(responseLength)];
            await stream.ReadExactlyAsync(response);
            Assert.Equal(IPAddress.Parse("10.1.2.3"), DnsTestPacket.FirstAddress(response));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_rejects_queries_when_dns_is_stopped()
    {
        await using var host = new DnsServerHost();
        Assert.False(host.Status.Running);
        await host.StopAsync();
        Assert.False(host.Status.Running);
    }

    private sealed class FakeDnsUpstream : IAsyncDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _sourcePorts = new();
        private int _requests;

        public FakeDnsUpstream(int delayMs = 0)
        {
            DelayMs = delayMs;
            _loop = Task.Run(LoopAsync);
        }

        public int DelayMs { get; }
        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public int Requests => Volatile.Read(ref _requests);
        public int[] SourcePorts => _sourcePorts.ToArray();

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await _udp.ReceiveAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }

                Interlocked.Increment(ref _requests);
                if (received.RemoteEndPoint is IPEndPoint source)
                    _sourcePorts.Enqueue(source.Port);
                if (DelayMs > 0)
                {
                    try { await Task.Delay(DelayMs, _cts.Token); }
                    catch { return; }
                }
                if (!DnsWire.TryParseMessage(received.Buffer, out var layout))
                    continue;

                var response = DnsWire.BuildResponse(received.Buffer, layout.Question,
                    new[] { new DnsAnswerRecord(1, 60, new byte[] { 203, 0, 113, 9 }) });
                try
                {
                    await _udp.SendAsync(response, received.RemoteEndPoint, _cts.Token);
                }
                catch
                {
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _udp.Dispose();
            try { await _loop; } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    private sealed class FakeTcpDnsUpstream : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _clients = new();
        private readonly Task _loop;
        private int _connections;
        private int _requests;

        public FakeTcpDnsUpstream()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Connections => Volatile.Read(ref _connections);
        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                lock (_clients)
                    _clients.Add(client);
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                while (!_cts.IsCancellationRequested)
                {
                    var lengthBuffer = new byte[2];
                    if (!await ReadExactAsync(stream, lengthBuffer))
                        return;

                    var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
                    if (length < DnsWire.HeaderLength)
                        return;

                    var query = new byte[length];
                    if (!await ReadExactAsync(stream, query))
                        return;

                    Interlocked.Increment(ref _requests);
                    if (!DnsWire.TryParseMessage(query, out var layout))
                        return;

                    var response = DnsWire.BuildResponse(query, layout.Question,
                        new[] { new DnsAnswerRecord(1, 60, new byte[] { 203, 0, 113, 10 }) });
                    var responseLength = new byte[2];
                    BinaryPrimitives.WriteUInt16BigEndian(responseLength, (ushort)response.Length);
                    await stream.WriteAsync(responseLength);
                    await stream.WriteAsync(response);
                }
            }
            catch
            {
                // 客户端断开或服务停止
            }
            finally
            {
                client.Dispose();
            }
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer[read..]);
                if (count == 0)
                    return false;
                read += count;
            }
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            lock (_clients)
            {
                foreach (var client in _clients)
                    client.Dispose();
                _clients.Clear();
            }
            try { await _loop; } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}

internal static class DnsTestPacket
{
    public static byte[] Query(string name, ushort type, ushort id = 0x1234)
    {
        var nameBytes = DnsWire.EncodeName(name);
        var query = new byte[12 + nameBytes.Length + 4];
        DnsWire.WriteU16(query, 0, id);
        DnsWire.WriteU16(query, 2, 0x0100);
        DnsWire.WriteU16(query, 4, 1);
        nameBytes.AsSpan().CopyTo(query.AsSpan(12));
        var offset = 12 + nameBytes.Length;
        DnsWire.WriteU16(query, offset, type);
        DnsWire.WriteU16(query, offset + 2, 1);
        return query;
    }

    public static async Task<byte[]> QueryUdpAsync(int port, string name, ushort type)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ReceiveTimeout = 5000;
        var query = Query(name, type);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        await udp.SendAsync(query, query.Length, endpoint);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await udp.ReceiveAsync(cts.Token);
        return received.Buffer;
    }

    public static async Task<byte[]> QueryTcpAsync(string endpoint, string name, ushort type)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPEndPoint.Parse(endpoint));
        var stream = client.GetStream();
        var query = Query(name, type);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)query.Length);
        await stream.WriteAsync(length);
        await stream.WriteAsync(query);

        var responseLength = new byte[2];
        await stream.ReadExactlyAsync(responseLength);
        var response = new byte[BinaryPrimitives.ReadUInt16BigEndian(responseLength)];
        await stream.ReadExactlyAsync(response);
        return response;
    }

    public static IPAddress FirstAddress(byte[] response)
    {
        Assert.True(DnsWire.TryParseMessage(response, out var layout));
        var answer = Assert.Single(layout.Answers);
        Assert.Equal(1, answer.Type);
        return new IPAddress(response.AsSpan(answer.RdataOffset, answer.RdataLength).ToArray());
    }
}
