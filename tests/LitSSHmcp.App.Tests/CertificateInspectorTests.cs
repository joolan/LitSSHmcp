using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LitSSHmcp.App.Services.Toolbox;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class CertificateInspectorTests
{
    [Fact]
    public async Task Check_reads_remote_certificate_without_trusting_it_in_test()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Windows SslStream 服务端不接受纯临时密钥，测试时导入到当前用户密钥容器。
        using var certificate = new X509Certificate2(
            ephemeral.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate
            }, cts.Token);
        }, cts.Token);

        try
        {
            var result = await new CertificateInspector().CheckAsync(
                $"https://localhost:{port}", TimeSpan.FromSeconds(5), cts.Token);

            await server;
            Assert.True(result.Success, result.Error);
            Assert.Contains("localhost", result.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(port, result.Port);
            Assert.Equal("127.0.0.1", result.IpAddress);
            Assert.NotNull(result.NotAfter);
            Assert.InRange(result.DaysRemaining ?? -1, 20, 31);
            Assert.False(string.IsNullOrWhiteSpace(result.TlsProtocol));
        }
        finally
        {
            listener.Stop();
        }
    }
}
