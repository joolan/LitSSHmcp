using System.IO;
using System.Net;
using System.Threading;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.Views.Tools;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class WakeOnLanServiceTests
{
    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("AABB.CCDD.EEFF")]
    [InlineData("aa bb cc dd ee ff")]
    public void Mac_normalization_accepts_common_formats(string input)
    {
        Assert.True(WakeOnLanService.TryNormalizeMac(input, out var normalized));
        Assert.Equal("AA:BB:CC:DD:EE:FF", normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("GG:BB:CC:DD:EE:FF")]
    [InlineData("not-a-mac")]
    public void Mac_normalization_rejects_invalid_values(string input)
    {
        Assert.False(WakeOnLanService.TryNormalizeMac(input, out _));
        Assert.Throws<FormatException>(() => WakeOnLanService.NormalizeMac(input));
    }

    [Fact]
    public void Magic_packet_contains_sync_header_and_sixteen_mac_repeats()
    {
        var packet = WakeOnLanService.BuildMagicPacket("AA:BB:CC:DD:EE:FF");

        Assert.Equal(102, packet.Length);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, packet.Take(6));
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF },
                packet.Skip(6 + i * 6).Take(6));
        }
    }

    [Fact]
    public void Arp_parser_reads_rows_and_ignores_headers()
    {
        const string output =
            "\r\n接口: 192.168.1.10 --- 0x9\r\n" +
            "  Internet 地址         物理地址              类型\r\n" +
            "  192.168.1.1           a4-5e-60-11-22-33     动态\r\n" +
            "  192.168.1.20          00:1a:2b:3c:4d:5e     动态\r\n" +
            "  192.168.1.255         ff-ff-ff-ff-ff-ff     静态\r\n";

        var rows = WakeOnLanService.ParseArpTable(output);

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.IpAddress == "192.168.1.1" && r.MacAddress == "a4-5e-60-11-22-33");
        Assert.Contains(rows, r => r.IpAddress == "192.168.1.20" && r.MacAddress == "00:1a:2b:3c:4d:5e");
    }

    [Fact]
    public void Scan_hosts_excludes_network_broadcast_and_local_address()
    {
        var adapter = new WakeOnLanAdapter
        {
            Name = "test",
            Description = "test",
            Address = IPAddress.Parse("192.168.1.10"),
            Mask = IPAddress.Parse("255.255.255.0"),
            Network = IPAddress.Parse("192.168.1.0"),
            Broadcast = IPAddress.Parse("192.168.1.255"),
            PrefixLength = 24
        };

        var hosts = WakeOnLanService.GetScanHosts(adapter);

        Assert.Equal(253, hosts.Count);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.10"), hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), hosts);
    }

    [Fact]
    public void Scan_hosts_rejects_large_subnets()
    {
        var adapter = new WakeOnLanAdapter
        {
            Name = "test",
            Description = "test",
            Address = IPAddress.Parse("10.0.1.10"),
            Mask = IPAddress.Parse("255.255.0.0"),
            Network = IPAddress.Parse("10.0.0.0"),
            Broadcast = IPAddress.Parse("10.0.255.255"),
            PrefixLength = 16
        };

        Assert.Throws<InvalidOperationException>(() => WakeOnLanService.GetScanHosts(adapter));
    }

    [Fact]
    public async Task Store_round_trips_hosts_and_normalizes_defaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "litssh-wol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "wake-on-lan.json");
            var store = new WakeOnLanStore(path);
            await store.SaveAsync(new WakeOnLanDocument
            {
                Hosts = new List<WakeOnLanHost>
                {
                    new()
                    {
                        Name = "  NAS  ",
                        MacAddress = " AA:BB:CC:DD:EE:FF ",
                        Port = 0
                    }
                }
            });

            var loaded = await store.LoadAsync();

            var host = Assert.Single(loaded.Hosts);
            Assert.Equal("NAS", host.Name);
            Assert.Equal("AA:BB:CC:DD:EE:FF", host.MacAddress);
            Assert.Equal(WakeOnLanService.DefaultPort, host.Port);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Wake_window_parses_xaml()
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new WakeOnLanToolWindow();
                window.Close();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(captured);
    }
}
