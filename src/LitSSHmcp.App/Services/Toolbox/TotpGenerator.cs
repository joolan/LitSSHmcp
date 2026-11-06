using System.Security.Cryptography;
using System.Text;

namespace LitSSHmcp.App.Services.Toolbox;

public sealed record TotpCode(string Code, int RemainingSeconds, double Progress);

public static class TotpGenerator
{
    public static TotpCode Generate(PasswordTotpConfig config, DateTimeOffset? now = null)
    {
        if (config.IsHotp)
        {
            var hotp = GenerateHotp(config, config.Counter);
            return new TotpCode(hotp, 0, 0);
        }

        var period = Math.Max(1, config.Period);
        var timestamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var counter = timestamp / period;
        var remaining = (int)(period - timestamp % period);
        var progress = remaining / (double)period;
        var code = ComputeCode(config, counter);
        return new TotpCode(code, remaining, progress);
    }

    public static string GenerateHotp(PasswordTotpConfig config, long counter) =>
        ComputeCode(config, counter);

    private static string ComputeCode(PasswordTotpConfig config, long counter)
    {
        var digits = Math.Clamp(config.Digits, 6, 8);
        var key = DecodeBase32(config.Secret);
        if (key.Length == 0)
            return string.Empty;

        Span<byte> counterBytes = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counterBytes[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        using HMAC hmac = config.Algorithm.ToUpperInvariant() switch
        {
            "SHA256" => new HMACSHA256(key),
            "SHA512" => new HMACSHA512(key),
            _ => new HMACSHA1(key)
        };
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) |
                     ((hash[offset + 1] & 0xFF) << 16) |
                     ((hash[offset + 2] & 0xFF) << 8) |
                     (hash[offset + 3] & 0xFF);
        var modulo = (int)Math.Pow(10, digits);
        return (binary % modulo).ToString(new string('0', digits));
    }

    public static bool TryParseUri(string value, out PasswordTotpConfig? config)
    {
        config = null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "otpauth", StringComparison.OrdinalIgnoreCase))
            return false;
        var isHotp = string.Equals(uri.Host, "hotp", StringComparison.OrdinalIgnoreCase);
        if (!isHotp && !string.Equals(uri.Host, "totp", StringComparison.OrdinalIgnoreCase))
            return false;

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var secret = query["secret"];
        if (string.IsNullOrWhiteSpace(secret))
            return false;

        var label = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        var issuer = query["issuer"] ?? string.Empty;
        var account = label;
        if (label.Contains(':'))
        {
            var parts = label.Split(':', 2);
            if (string.IsNullOrWhiteSpace(issuer))
                issuer = parts[0].Trim();
            account = parts[1].Trim();
        }

        config = new PasswordTotpConfig
        {
            Secret = NormalizeSecret(secret),
            Issuer = issuer,
            Account = account,
            Algorithm = (query["algorithm"] ?? "SHA1").ToUpperInvariant(),
            Digits = int.TryParse(query["digits"], out var digits) ? Math.Clamp(digits, 6, 8) : 6,
            Period = int.TryParse(query["period"], out var period) ? Math.Max(1, period) : 30,
            IsHotp = isHotp,
            Counter = long.TryParse(query["counter"], out var counter) ? Math.Max(0, counter) : 0
        };
        return true;
    }

    public static string NormalizeSecret(string secret) =>
        secret.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .TrimEnd('=')
            .ToUpperInvariant();

    private static byte[] DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var input = NormalizeSecret(value);
        if (input.Length == 0)
            return Array.Empty<byte>();

        var output = new List<byte>(input.Length * 5 / 8);
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var c in input)
        {
            var index = alphabet.IndexOf(c);
            if (index < 0)
                continue;
            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        return output.ToArray();
    }
}
