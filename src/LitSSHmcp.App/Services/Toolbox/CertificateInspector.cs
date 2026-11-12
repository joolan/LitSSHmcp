using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace LitSSHmcp.App.Services.Toolbox;

/// <summary>
/// 远程 TLS 证书检测：建立 TCP + TLS 握手，读取叶子证书、证书链、有效期和校验错误。
/// 仅用于用户输入的目标，不保存任何证书或连接信息。
/// </summary>
public sealed class CertificateInspector
{
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    public async Task<CertificateCheckResult> CheckAsync(
        string input,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!CertificateTargetParser.TryParse(input, out var target, out var parseError) || target is null)
            return Error(input, parseError, severity: "Error", status: "地址无效");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        X509Certificate2? leaf = null;
        var policyErrors = SslPolicyErrors.None;
        string chainText = string.Empty;
        var ipAddress = string.Empty;

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(target.Host, target.Port, timeoutCts.Token).ConfigureAwait(false);
            var remoteAddress = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remoteAddress is { IsIPv4MappedToIPv6: true })
                remoteAddress = remoteAddress.MapToIPv4();
            ipAddress = remoteAddress?.ToString() ?? string.Empty;

            using var ssl = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, chain, errors) =>
                {
                    policyErrors = errors;
                    chainText = FormatChain(chain);
                    if (certificate is not null)
                        leaf = new X509Certificate2(certificate);
                    return true;   // 继续握手以读取证书详情；校验结果单独展示
                });

            var options = new SslClientAuthenticationOptions
            {
                TargetHost = target.Host,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            };

            await ssl.AuthenticateAsClientAsync(options, timeoutCts.Token).ConfigureAwait(false);
            leaf ??= ssl.RemoteCertificate as X509Certificate2;
            if (leaf is null)
                return Error(input, "TLS 握手成功但没有返回服务器证书", "Error", "无证书");

            return BuildResult(target, leaf, ssl.SslProtocol.ToString(), chainText, policyErrors, ipAddress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error(input, "检测已取消", "Error", "已取消", target);
        }
        catch (OperationCanceledException)
        {
            return Error(input, $"连接或 TLS 握手超时（{timeout.TotalSeconds:0}s）", "Error", "超时", target);
        }
        catch (Exception ex)
        {
            return Error(input, DescribeException(ex), "Error", "连接失败", target);
        }
        finally
        {
            leaf?.Dispose();
        }
    }

    private static CertificateCheckResult BuildResult(
        CertificateTarget target,
        X509Certificate2 certificate,
        string tlsProtocol,
        string chain,
        SslPolicyErrors policyErrors,
        string ipAddress)
    {
        var now = DateTime.UtcNow;
        var notAfter = certificate.NotAfter.ToUniversalTime();
        var days = (int)Math.Floor((notAfter - now).TotalDays);
        var failedValidation = policyErrors != SslPolicyErrors.None;
        var expired = notAfter < now || certificate.NotBefore.ToUniversalTime() > now;
        var severity = failedValidation || expired
            ? "Error"
            : days <= 30 ? "Warning" : "Ok";
        var status = severity switch
        {
            "Error" => failedValidation ? "证书校验失败" : "证书已过期",
            "Warning" => "即将过期",
            _ => "正常"
        };

        return new CertificateCheckResult
        {
            Input = target.Input,
            Host = target.Host,
            Port = target.Port,
            IpAddress = ipAddress,
            Success = true,
            StatusText = status,
            Severity = severity,
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            NotBefore = certificate.NotBefore.ToLocalTime(),
            NotAfter = certificate.NotAfter.ToLocalTime(),
            DaysRemaining = days,
            TlsProtocol = tlsProtocol,
            SignatureAlgorithm = certificate.SignatureAlgorithm.FriendlyName ?? certificate.SignatureAlgorithm.Value ?? string.Empty,
            PublicKeyAlgorithm = certificate.PublicKey.Oid.FriendlyName ?? certificate.PublicKey.Oid.Value ?? string.Empty,
            Thumbprint = certificate.Thumbprint,
            SerialNumber = certificate.SerialNumber,
            SubjectAlternativeNames = ReadSubjectAlternativeNames(certificate),
            Chain = chain,
            PolicyErrors = policyErrors == SslPolicyErrors.None ? string.Empty : policyErrors.ToString(),
            Error = failedValidation || expired ? DescribePolicyError(policyErrors, expired) : string.Empty
        };
    }

    private static string FormatChain(X509Chain? chain)
    {
        if (chain is null || chain.ChainElements.Count == 0)
            return string.Empty;

        return string.Join(" -> ", chain.ChainElements
            .Cast<X509ChainElement>()
            .Select(element => element.Certificate.Subject));
    }

    private static string ReadSubjectAlternativeNames(X509Certificate2 certificate)
    {
        try
        {
            var extension = certificate.Extensions
                .Cast<X509Extension>()
                .FirstOrDefault(item => string.Equals(item.Oid?.Value, SubjectAlternativeNameOid, StringComparison.Ordinal));
            return extension?.Format(multiLine: false) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DescribePolicyError(SslPolicyErrors errors, bool expired)
    {
        var parts = new List<string>();
        if (expired)
            parts.Add("证书不在有效期内或已过期");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            parts.Add("主机名与证书 SAN/CN 不匹配");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            parts.Add("证书链不受信任或不完整");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            parts.Add("服务器未提供证书");
        return parts.Count == 0 ? "证书校验失败" : string.Join("；", parts);
    }

    private static string DescribeException(Exception ex) => ex switch
    {
        SocketException socket => $"网络连接失败：{socket.SocketErrorCode}",
        AuthenticationException auth => $"TLS 握手失败：{auth.Message}",
        _ => ex.Message
    };

    private static CertificateCheckResult Error(
        string input,
        string error,
        string severity,
        string status,
        CertificateTarget? target = null) => new()
        {
            Input = input,
            Host = target?.Host ?? string.Empty,
            Port = target?.Port ?? 0,
            Success = false,
            StatusText = status,
            Severity = severity,
            Error = error
        };
}
