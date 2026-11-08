using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace LitSSHmcp.Core.Services.SSH;

/// <summary>
/// SSH 密钥生成：优先调用系统 <c>ssh-keygen</c>（支持 ed25519/rsa，产出标准 OpenSSH 私钥 + .pub）；
/// 系统没有 ssh-keygen 时回退用 .NET 生成 RSA（PKCS#1 PEM 私钥 + OpenSSH .pub 公钥）。
/// </summary>
public static class SshKeyGenerator
{
    public sealed record GeneratedKey(string PrivateKeyPath, string PublicKeyPath, string PublicKeyOpenSsh, bool UsedSshKeyGen);

    /// <summary>
    /// 生成密钥对。<paramref name="keyType"/> = <c>ed25519</c> 或 <c>rsa</c>；<paramref name="passphrase"/> 空=无口令。
    /// 目标文件已存在时会被覆盖（避免 ssh-keygen 的交互提示）。
    /// </summary>
    public static GeneratedKey Generate(string directory, string fileName, string keyType = "ed25519",
        string? passphrase = null, string comment = "litssh")
    {
        Directory.CreateDirectory(directory);
        var privatePath = Path.Combine(directory, fileName);
        var publicPath = privatePath + ".pub";

        // 覆盖前先删除，避免 ssh-keygen 提示 "Overwrite (y/n)?"
        TryDelete(privatePath);
        TryDelete(publicPath);

        if (TrySshKeyGen(keyType, privatePath, passphrase, comment, out var sshPub))
            return new GeneratedKey(privatePath, publicPath, sshPub, true);

        return GenerateRsaFallback(privatePath, comment);
    }

    private static bool TrySshKeyGen(string keyType, string privatePath, string? passphrase, string comment, out string publicKey)
    {
        publicKey = string.Empty;
        var type = string.Equals(keyType, "rsa", StringComparison.OrdinalIgnoreCase) ? "rsa" : "ed25519";
        string? askpassDir = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh-keygen",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(type);
            if (type == "rsa")
            {
                psi.ArgumentList.Add("-b");
                psi.ArgumentList.Add("4096");
            }
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(privatePath);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(comment);

            if (string.IsNullOrEmpty(passphrase))
            {
                psi.ArgumentList.Add("-N");
                psi.ArgumentList.Add(string.Empty);
            }
            else
            {
                // 不把口令放进 argv：用 SSH_ASKPASS 临时脚本从环境变量读取（base64 以兼容换行/特殊字符）。
                askpassDir = Path.Combine(Path.GetTempPath(), "litssh-askpass-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(askpassDir);
                var askpassPath = Path.Combine(askpassDir, "askpass.cmd");
                File.WriteAllText(askpassPath,
                    "@echo off\r\npowershell -NoProfile -NonInteractive -Command \"$b=[Convert]::FromBase64String($env:LITSSH_ASKPASS_B64);[Console]::Out.Write([Text.Encoding]::UTF8.GetString($b))\"\r\n",
                    new UTF8Encoding(false));
                psi.Environment["SSH_ASKPASS"] = askpassPath;
                psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
                psi.Environment["DISPLAY"] = ":0";
                psi.Environment["LITSSH_ASKPASS_B64"] =
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(passphrase));
            }

            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.StandardInput.Close();
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(20000);

            var pubPath = privatePath + ".pub";
            if (process.ExitCode == 0 && File.Exists(privatePath) && File.Exists(pubPath))
            {
                publicKey = File.ReadAllText(pubPath).Trim();
                TryRestrictPrivateKey(privatePath);
                return true;
            }
            return false;
        }
        catch
        {
            // ssh-keygen 不存在或执行失败 → 回退
            return false;
        }
        finally
        {
            if (askpassDir is not null)
            {
                try { Directory.Delete(askpassDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    private static GeneratedKey GenerateRsaFallback(string privatePath, string comment)
    {
        using var rsa = RSA.Create(4096);
        var privatePem = rsa.ExportRSAPrivateKeyPem();   // PKCS#1: -----BEGIN RSA PRIVATE KEY-----
        File.WriteAllText(privatePath, privatePem, new UTF8Encoding(false));
        TryRestrictPrivateKey(privatePath);

        var publicKey = ToOpenSshRsaPublicKey(rsa.ExportParameters(false), comment);
        var publicPath = privatePath + ".pub";
        File.WriteAllText(publicPath, publicKey + "\n", new UTF8Encoding(false));
        return new GeneratedKey(privatePath, publicPath, publicKey, false);
    }

    /// <summary>把 RSA 公钥编码为 OpenSSH 公钥文本（<c>ssh-rsa AAAA... comment</c>）。</summary>
    private static string ToOpenSshRsaPublicKey(RSAParameters pub, string comment)
    {
        using var ms = new MemoryStream();
        WriteSshString(ms, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteSshMpint(ms, pub.Exponent!);
        WriteSshMpint(ms, pub.Modulus!);
        return $"ssh-rsa {Convert.ToBase64String(ms.ToArray())} {comment}";
    }

    private static void WriteSshString(Stream s, byte[] data)
    {
        var len = BitConverter.GetBytes((uint)data.Length);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(len);
        s.Write(len, 0, len.Length);
        s.Write(data, 0, data.Length);
    }

    private static void WriteSshMpint(Stream s, byte[] value)
    {
        // mpint：最高位为 1 时前置一个 0 字节，避免被当作负数。
        byte[] data;
        if (value.Length > 0 && (value[0] & 0x80) != 0)
        {
            data = new byte[value.Length + 1];
            Array.Copy(value, 0, data, 1, value.Length);
        }
        else
        {
            data = value;
        }
        WriteSshString(s, data);
    }

    private static void TryRestrictPrivateKey(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* ignore */ }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
