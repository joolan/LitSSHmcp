using System.Text;
using LitSSHmcp.App.Services.Toolbox;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ToolboxUtilityTests
{
    [Fact]
    public void Netstat_parser_handles_tcp_udp_and_ipv6()
    {
        const string output =
            "  TCP    0.0.0.0:135          0.0.0.0:0              LISTENING       4\r\n" +
            "  TCP    [::]:443             [::]:0                 LISTENING       12345\r\n" +
            "  TCP    127.0.0.1:5000       127.0.0.1:6000         ESTABLISHED     23456\r\n" +
            "  UDP    0.0.0.0:500          *:*                                    34567\r\n";

        var entries = LocalPortProcessService.Parse(output);

        Assert.Contains(entries, e => e.Protocol == "TCP" && e.LocalPort == 135 && e.IsListening);
        Assert.Contains(entries, e => e.Protocol == "TCP" && e.LocalAddress == "::" && e.LocalPort == 443);
        Assert.Contains(entries, e => e.Protocol == "TCP" && e.LocalPort == 5000
                                      && e.RemotePort == 6000 && !e.IsListening);
        Assert.Contains(entries, e => e.Protocol == "UDP" && e.LocalPort == 500 && e.IsListening);
    }

    [Fact]
    public void Ssh_public_key_parser_reports_type_comment_and_fingerprint()
    {
        var blob = Encoding.UTF8.GetBytes("not-a-real-key-blob");
        var key = "ssh-ed25519 " + Convert.ToBase64String(blob) + " ops@example";
        var info = new SshKeyService().ParsePublicKey(key);

        Assert.Equal("ssh-ed25519", info.Algorithm);
        Assert.Equal("ops@example", info.Comment);
        Assert.StartsWith("SHA256:", info.Fingerprint);
        Assert.Equal(key, info.KeyText);
    }

    [Fact]
    public void Ssh_public_key_parser_rejects_invalid_input()
    {
        Assert.Throws<FormatException>(() => new SshKeyService().ParsePublicKey("not-a-key"));
        Assert.Throws<FormatException>(() => new SshKeyService().ParsePublicKey("ssh-rsa !!! bad"));
    }
}
