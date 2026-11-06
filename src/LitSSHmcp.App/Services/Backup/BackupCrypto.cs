using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LitSSHmcp.App.Services.Backup;

/// <summary>
/// 加密备份文件格式：LSBK1 + version + iterations + salt + 分块 AES-GCM。
/// </summary>
public static class BackupCrypto
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LSBK1");
    private const int Version = 1;
    private const int ChunkSize = 1024 * 1024;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    public const int DefaultIterations = 200_000;

    public static bool IsEncrypted(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var magic = new byte[Magic.Length];
            return stream.Read(magic, 0, magic.Length) == magic.Length &&
                   magic.AsSpan().SequenceEqual(Magic);
        }
        catch
        {
            return false;
        }
    }

    public static void EncryptFile(string inputPath, string outputPath, string password,
        int iterations = DefaultIterations, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("备份密码不能为空。", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Math.Max(1000, iterations),
            HashAlgorithmName.SHA256, 32);
        try
        {
            using var input = File.OpenRead(inputPath);
            using var output = File.Create(outputPath);
            using var writer = new BinaryWriter(output);
            using var aes = new AesGcm(key, TagSize);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(Math.Max(1000, iterations));
            writer.Write(salt);

            var buffer = new byte[ChunkSize];
            var chunkIndex = 0L;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                var plain = new byte[read];
                Buffer.BlockCopy(buffer, 0, plain, 0, read);
                var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                var cipher = new byte[read];
                var tag = new byte[TagSize];
                aes.Encrypt(nonce, plain, cipher, tag, BitConverter.GetBytes(chunkIndex));
                writer.Write(nonce);
                writer.Write(read);
                writer.Write(tag);
                writer.Write(cipher);
                chunkIndex++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void DecryptFile(string inputPath, string outputPath, string password,
        CancellationToken ct = default)
    {
        using var input = File.OpenRead(inputPath);
        using var reader = new BinaryReader(input);
        var magic = reader.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("不是有效的加密备份文件。");
        _ = reader.ReadInt32();
        var iterations = Math.Max(1000, reader.ReadInt32());
        var salt = reader.ReadBytes(16);
        if (salt.Length != 16)
            throw new InvalidDataException("备份文件头不完整。");

        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations,
            HashAlgorithmName.SHA256, 32);
        try
        {
            using var output = File.Create(outputPath);
            using var aes = new AesGcm(key, TagSize);
            var chunkIndex = 0L;
            while (input.Position < input.Length)
            {
                ct.ThrowIfCancellationRequested();
                var nonce = reader.ReadBytes(NonceSize);
                var length = reader.ReadInt32();
                var tag = reader.ReadBytes(TagSize);
                var cipher = reader.ReadBytes(length);
                if (nonce.Length != NonceSize || tag.Length != TagSize ||
                    cipher.Length != length || length < 0)
                    throw new InvalidDataException("备份文件不完整。");

                var plain = new byte[length];
                aes.Decrypt(nonce, cipher, tag, plain, BitConverter.GetBytes(chunkIndex));
                output.Write(plain, 0, plain.Length);
                chunkIndex++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
