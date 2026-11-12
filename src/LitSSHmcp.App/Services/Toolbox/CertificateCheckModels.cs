using System.Globalization;
using System.Text;

namespace LitSSHmcp.App.Services.Toolbox;

public sealed class CertificateTarget
{
    public CertificateTarget(string input, string host, int port)
    {
        Input = input;
        Host = host;
        Port = port;
    }

    public string Input { get; }
    public string Host { get; }
    public int Port { get; }
    public string Display => $"{Host}:{Port}";
}

public static class CertificateTargetParser
{
    /// <summary>
    /// 支持一行一个 URL / 主机名：
    /// https://example.com、example.com、example.com:8443、https://example.com:8443/path。
    /// 裸主机默认按 HTTPS 443 处理；http:// 不进行 TLS 检测。
    /// </summary>
    public static bool TryParse(string? input, out CertificateTarget? target, out string error)
    {
        target = null;
        error = string.Empty;
        var raw = (input ?? string.Empty).Trim().Trim('"', '\'');
        if (raw.Length == 0)
        {
            error = "地址为空";
            return false;
        }

        var candidate = raw.Contains("://", StringComparison.Ordinal) ? raw : "https://" + raw;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            error = "不是有效的 URL 或主机名";
            return false;
        }

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            error = "仅支持 https:// 或 wss:// 地址（裸主机默认 HTTPS）";
            return false;
        }

        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "缺少主机名";
            return false;
        }

        var port = uri.IsDefaultPort || uri.Port <= 0 ? 443 : uri.Port;
        target = new CertificateTarget(raw, host, port);
        return true;
    }

    public static IReadOnlyList<string> SplitInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // CSV 常见第一列为 URL；保留完整 URL 中的逗号（实际上 URL 很少含逗号）。
            var value = line.Trim().Trim('"', '\'');
            if (value.Length == 0 || value.StartsWith('#'))
                continue;

            var firstComma = value.IndexOf(',');
            if (firstComma > 0 && !value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                value = value[..firstComma].Trim().Trim('"', '\'');

            if (seen.Add(value))
                result.Add(value);
        }

        return result;
    }
}

public sealed class CertificateCheckResult
{
    public string Input { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string StatusText { get; init; } = "错误";
    public string Severity { get; init; } = "Error";
    public string Subject { get; init; } = string.Empty;
    public string Issuer { get; init; } = string.Empty;
    public DateTime? NotBefore { get; init; }
    public DateTime? NotAfter { get; init; }
    public int? DaysRemaining { get; init; }
    public string TlsProtocol { get; init; } = string.Empty;
    public string SignatureAlgorithm { get; init; } = string.Empty;
    public string PublicKeyAlgorithm { get; init; } = string.Empty;
    public string Thumbprint { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string SubjectAlternativeNames { get; init; } = string.Empty;
    public string Chain { get; init; } = string.Empty;
    public string PolicyErrors { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public DateTime CheckedAt { get; init; } = DateTime.Now;

    public string TargetText => string.IsNullOrWhiteSpace(Host) ? Input : $"{Host}:{Port}";
    public string ExpiryText => NotAfter?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
    public string DaysText => DaysRemaining switch
    {
        null => "-",
        < 0 => $"已过期 {Math.Abs(DaysRemaining.Value)} 天",
        _ => $"{DaysRemaining.Value} 天"
    };

    public string Details
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine($"目标：{Input}");
            if (!string.IsNullOrWhiteSpace(IpAddress))
                sb.AppendLine($"IP：{IpAddress}");
            sb.AppendLine($"检测时间：{CheckedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"状态：{StatusText}");
            if (!string.IsNullOrWhiteSpace(Error))
                sb.AppendLine($"错误：{Error}");
            if (!string.IsNullOrWhiteSpace(Subject))
                sb.AppendLine($"主题：{Subject}");
            if (!string.IsNullOrWhiteSpace(Issuer))
                sb.AppendLine($"颁发者：{Issuer}");
            if (NotAfter is not null)
                sb.AppendLine($"有效期：{NotBefore:yyyy-MM-dd HH:mm} ~ {NotAfter:yyyy-MM-dd HH:mm}（剩余 {DaysText}）");
            if (!string.IsNullOrWhiteSpace(TlsProtocol))
                sb.AppendLine($"TLS：{TlsProtocol}");
            if (!string.IsNullOrWhiteSpace(SignatureAlgorithm))
                sb.AppendLine($"签名算法：{SignatureAlgorithm}");
            if (!string.IsNullOrWhiteSpace(PublicKeyAlgorithm))
                sb.AppendLine($"公钥算法：{PublicKeyAlgorithm}");
            if (!string.IsNullOrWhiteSpace(Thumbprint))
                sb.AppendLine($"指纹：{Thumbprint}");
            if (!string.IsNullOrWhiteSpace(SerialNumber))
                sb.AppendLine($"序列号：{SerialNumber}");
            if (!string.IsNullOrWhiteSpace(SubjectAlternativeNames))
                sb.AppendLine($"SAN：{SubjectAlternativeNames}");
            if (!string.IsNullOrWhiteSpace(Chain))
                sb.AppendLine($"证书链：{Chain}");
            if (!string.IsNullOrWhiteSpace(PolicyErrors))
                sb.AppendLine($"校验错误：{PolicyErrors}");
            return sb.ToString().TrimEnd();
        }
    }

    public static string CsvHeader =>
        "input,host,ip,port,status,severity,subject,issuer,not_before,not_after,days_remaining,tls,error";

    public string ToCsvRow() => string.Join(",",
        Csv(Input), Csv(Host), Csv(IpAddress), Port.ToString(CultureInfo.InvariantCulture), Csv(StatusText),
        Csv(Severity), Csv(Subject), Csv(Issuer),
        Csv(NotBefore?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty),
        Csv(NotAfter?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty),
        DaysRemaining?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        Csv(TlsProtocol), Csv(Error));

    private static string Csv(string? value)
        => "\"" + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
