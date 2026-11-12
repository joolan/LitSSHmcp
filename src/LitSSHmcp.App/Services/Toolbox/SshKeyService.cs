using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Services.Toolbox;

public sealed record SshKeyAlgorithm(string Id, string Title, string KeyType, string? Bits, string DefaultFileName);

public sealed class SshKeyGenerationResult
{
    public required string PrivateKeyPath { get; init; }
    public required string PublicKeyPath { get; init; }
    public required string PublicKey { get; init; }
    public required string Fingerprint { get; init; }
    public required string Algorithm { get; init; }
    public string Comment { get; init; } = string.Empty;
}

public sealed class SshPublicKeyInfo
{
    public required string Algorithm { get; init; }
    public required string KeyText { get; init; }
    public required string Fingerprint { get; init; }
    public string Comment { get; init; } = string.Empty;
}

public static class SshKeyAlgorithms
{
    public static IReadOnlyList<SshKeyAlgorithm> All { get; } = new[]
    {
        new SshKeyAlgorithm("ed25519", "Ed25519（推荐）", "ed25519", null, "id_ed25519"),
        new SshKeyAlgorithm("rsa-3072", "RSA 3072", "rsa", "3072", "id_rsa_3072"),
        new SshKeyAlgorithm("rsa-4096", "RSA 4096", "rsa", "4096", "id_rsa_4096"),
        new SshKeyAlgorithm("ecdsa-256", "ECDSA P-256", "ecdsa", "256", "id_ecdsa_256"),
        new SshKeyAlgorithm("ecdsa-384", "ECDSA P-384", "ecdsa", "384", "id_ecdsa_384")
    };
}

/// <summary>
/// SSH 密钥助手：调用 Windows OpenSSH 的 ssh-keygen 生成 OpenSSH 格式密钥，
/// 并解析公钥类型、注释和 SHA256 指纹。不保存口令，不把私钥发送到网络。
/// </summary>
public sealed class SshKeyService
{
    public async Task<SshKeyGenerationResult> GenerateAsync(
        string privateKeyPath,
        SshKeyAlgorithm algorithm,
        string? comment,
        string? passphrase,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPath))
            throw new ArgumentException("请选择私钥保存路径。", nameof(privateKeyPath));

        privateKeyPath = Path.GetFullPath(privateKeyPath);
        var publicKeyPath = privateKeyPath + ".pub";
        if (File.Exists(privateKeyPath) || File.Exists(publicKeyPath))
            throw new IOException("目标密钥文件已存在，请换一个文件名。");

        var directory = Path.GetDirectoryName(privateKeyPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new IOException("私钥保存目录无效。");
        Directory.CreateDirectory(directory);

        var sshKeygen = FindSshKeygen();
        var startInfo = new ProcessStartInfo
        {
            FileName = sshKeygen,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(algorithm.KeyType);
        if (!string.IsNullOrWhiteSpace(algorithm.Bits))
        {
            startInfo.ArgumentList.Add("-b");
            startInfo.ArgumentList.Add(algorithm.Bits);
        }
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(privateKeyPath);
        startInfo.ArgumentList.Add("-N");
        startInfo.ArgumentList.Add(passphrase ?? string.Empty);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(comment) ? "litssh" : comment.Trim());
        startInfo.ArgumentList.Add("-q");

        using var process = NativeShell.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ssh-keygen.exe。");
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? $"ssh-keygen 退出码 {process.ExitCode}" : error.Trim());
        }

        var publicKey = (await File.ReadAllTextAsync(publicKeyPath, cancellationToken).ConfigureAwait(false)).Trim();
        var info = ParsePublicKey(publicKey);
        return new SshKeyGenerationResult
        {
            PrivateKeyPath = privateKeyPath,
            PublicKeyPath = publicKeyPath,
            PublicKey = info.KeyText,
            Fingerprint = info.Fingerprint,
            Algorithm = info.Algorithm,
            Comment = info.Comment
        };
    }

    public SshPublicKeyInfo ParsePublicKey(string text)
    {
        var lines = (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
        if (lines.Count == 0)
            throw new FormatException("没有可解析的 SSH 公钥。");

        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new FormatException("SSH 公钥格式应为：类型 Base64 [注释]。");

        var algorithm = parts[0];
        if (!algorithm.StartsWith("ssh-", StringComparison.OrdinalIgnoreCase)
            && !algorithm.StartsWith("ecdsa-", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("无法识别 SSH 公钥类型。");

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            throw new FormatException("SSH 公钥 Base64 内容无效。");
        }

        var fingerprint = "SHA256:" + ToBase64Url(SHA256.HashData(blob));
        return new SshPublicKeyInfo
        {
            Algorithm = algorithm,
            KeyText = lines[0],
            Comment = parts.Length >= 3 ? parts[2] : string.Empty,
            Fingerprint = fingerprint
        };
    }

    private static string FindSshKeygen()
    {
        var system = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keygen.exe");
        if (File.Exists(system))
            return system;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "ssh-keygen.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // 忽略无效 PATH 项
            }
        }

        throw new FileNotFoundException("未找到 ssh-keygen.exe，请安装 Windows OpenSSH Client。");
    }

    private static string ToBase64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
