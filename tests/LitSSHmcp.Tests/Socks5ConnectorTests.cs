using System.Net;
using System.Net.Sockets;
using System.Text;
using LitSSHmcp.Core.Services.Jenkins;
using Xunit;

namespace LitSSHmcp.Tests;

public class Socks5ConnectorTests
{
    [Fact]
    public async Task Connects_through_socks5_and_sends_original_target()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observedHost = string.Empty;
        var observedPort = 0;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();

            var greeting = new byte[3];
            await ReadExactlyAsync(stream, greeting);
            Assert.Equal(new byte[] { 0x05, 0x01, 0x00 }, greeting);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 });

            var header = new byte[5];
            await ReadExactlyAsync(stream, header);
            Assert.Equal(0x05, header[0]);
            Assert.Equal(0x03, header[3]);
            var hostLength = header[4];
            var host = new byte[hostLength];
            await ReadExactlyAsync(stream, host);
            observedHost = Encoding.UTF8.GetString(host);
            var portBytes = new byte[2];
            await ReadExactlyAsync(stream, portBytes);
            observedPort = (portBytes[0] << 8) | portBytes[1];

            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 });
        });

        try
        {
            await using var stream = await Socks5Connector.ConnectAsync(
                "127.0.0.1", port, "jenkins.example", 8443);
            Assert.NotNull(stream);
            await serverTask;

            Assert.Equal("jenkins.example", observedHost);
            Assert.Equal(8443, observedPort);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0)
                throw new IOException("unexpected end of stream");
            offset += read;
        }
    }
}
