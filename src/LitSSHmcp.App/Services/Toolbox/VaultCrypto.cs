using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LitSSHmcp.App.Services.Toolbox;

/// <summary>文件柜底层加密：AES-GCM；文件内容按 1MB 分块，防止大文件一次性读入内存。</summary>
internal static class VaultCrypto
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    private const int ChunkSize = 1024 * 1024;
    private static readonly byte[] FileMagic = Encoding.ASCII.GetBytes("LSV1");

    public static byte[] EncryptBytes(byte[] plaintext, byte[] key, byte[] associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plaintext, cipher, tag, associatedData);

        var result = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, result, NonceSize + TagSize, cipher.Length);
        return result;
    }

    public static byte[] DecryptBytes(byte[] payload, byte[] key, byte[] associatedData)
    {
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException("密文长度无效。");

        var nonce = payload.AsSpan(0, NonceSize).ToArray();
        var tag = payload.AsSpan(NonceSize, TagSize).ToArray();
        var cipher = payload.AsSpan(NonceSize + TagSize).ToArray();
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, TagSize))
            aes.Decrypt(nonce, cipher, tag, plain, associatedData);
        return plain;
    }

    public static byte[] WrapKey(byte[] keyToWrap, byte[] wrappingKey, Guid entryId)
        => EncryptBytes(keyToWrap, wrappingKey, EntryAad(entryId));

    public static byte[] UnwrapKey(byte[] wrapped, byte[] wrappingKey, Guid entryId)
        => DecryptBytes(wrapped, wrappingKey, EntryAad(entryId));

    private static byte[] EntryAad(Guid entryId) => Encoding.UTF8.GetBytes("file:" + entryId.ToString("N"));

    public static void EncryptFile(string inputPath, string outputPath, byte[] dataKey,
        Action<long>? onBytes = null, CancellationToken ct = default)
    {
        using var input = File.OpenRead(inputPath);
        using var output = File.Create(outputPath);
        using var writer = new BinaryWriter(output);
        using var aes = new AesGcm(dataKey, TagSize);

        writer.Write(FileMagic);
        writer.Write(ChunkSize);
        writer.Write(input.Length);

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
            onBytes?.Invoke(read);
        }
    }

    public static byte[] DecryptFileToBytes(string inputPath, byte[] dataKey, long maxBytes,
        CancellationToken ct = default)
    {
        using var input = File.OpenRead(inputPath);
        using var reader = new BinaryReader(input);
        using var aes = new AesGcm(dataKey, TagSize);

        var magic = reader.ReadBytes(FileMagic.Length);
        if (magic.Length != FileMagic.Length || !magic.SequenceEqual(FileMagic))
            throw new InvalidDataException("文件柜数据格式无效。");
        _ = reader.ReadInt32();
        var totalLength = reader.ReadInt64();
        if (totalLength < 0 || totalLength > maxBytes)
            throw new InvalidDataException("文件过大，无法在内存中打开。");

        using var output = new MemoryStream((int)totalLength);
        var written = 0L;
        var chunkIndex = 0L;
        while (written < totalLength)
        {
            ct.ThrowIfCancellationRequested();
            var nonce = reader.ReadBytes(NonceSize);
            var length = reader.ReadInt32();
            var tag = reader.ReadBytes(TagSize);
            var cipher = reader.ReadBytes(length);
            if (nonce.Length != NonceSize || tag.Length != TagSize || cipher.Length != length)
                throw new InvalidDataException("文件柜数据不完整。");

            var plain = new byte[length];
            aes.Decrypt(nonce, cipher, tag, plain, BitConverter.GetBytes(chunkIndex));
            output.Write(plain, 0, plain.Length);
            written += length;
            chunkIndex++;
        }
        return output.ToArray();
    }

    public static void EncryptBytesToFile(byte[] data, string outputPath, byte[] dataKey,
        Action<long>? onBytes = null, CancellationToken ct = default)
    {
        using var input = new MemoryStream(data, writable: false);
        using var output = File.Create(outputPath);
        using var writer = new BinaryWriter(output);
        using var aes = new AesGcm(dataKey, TagSize);

        writer.Write(FileMagic);
        writer.Write(ChunkSize);
        writer.Write((long)data.Length);

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
            onBytes?.Invoke(read);
        }
    }

    public static void DecryptFile(string inputPath, string outputPath, byte[] dataKey,
        Action<long>? onBytes = null, CancellationToken ct = default)
    {
        using var input = File.OpenRead(inputPath);
        using var reader = new BinaryReader(input);
        using var output = File.Create(outputPath);
        using var aes = new AesGcm(dataKey, TagSize);

        var magic = reader.ReadBytes(FileMagic.Length);
        if (magic.Length != FileMagic.Length || !magic.SequenceEqual(FileMagic))
            throw new InvalidDataException("文件柜数据格式无效。");
        _ = reader.ReadInt32();
        var totalLength = reader.ReadInt64();
        var written = 0L;
        var chunkIndex = 0L;

        while (written < totalLength)
        {
            ct.ThrowIfCancellationRequested();
            var nonce = reader.ReadBytes(NonceSize);
            var length = reader.ReadInt32();
            var tag = reader.ReadBytes(TagSize);
            var cipher = reader.ReadBytes(length);
            if (nonce.Length != NonceSize || tag.Length != TagSize || cipher.Length != length)
                throw new InvalidDataException("文件柜数据不完整。");

            var plain = new byte[length];
            aes.Decrypt(nonce, cipher, tag, plain, BitConverter.GetBytes(chunkIndex));
            output.Write(plain, 0, plain.Length);
            written += length;
            chunkIndex++;
            onBytes?.Invoke(length);
        }
    }
}
